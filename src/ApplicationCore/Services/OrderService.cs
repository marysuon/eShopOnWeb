using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Ardalis.GuardClauses;
using Microsoft.Azure.ServiceBus;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;
using Newtonsoft.Json;

namespace Microsoft.eShopWeb.ApplicationCore.Services;

public class OrderService : IOrderService
{
    private const string PlaceOrderToCosmosDbUrl = "https://orderitemsreserverfunc.azurewebsites.net/api/PlaceOrderToCosmosDb";
    private const string ServiceBusConnectionString = "Endpoint=sb://aaleastussb.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=VAb4oBV7OwKYo0IlMQYjZ5ynpUtl/FgvkZKrWK2GqdI=";
    private const string QueueName = "orders";

    private readonly IRepository<Order> _orderRepository;
    private readonly IUriComposer _uriComposer;
    private readonly IRepository<Basket> _basketRepository;
    private readonly IRepository<CatalogItem> _itemRepository;

    public OrderService(IRepository<Basket> basketRepository,
        IRepository<CatalogItem> itemRepository,
        IRepository<Order> orderRepository,
        IUriComposer uriComposer)
    {
        _orderRepository = orderRepository;
        _uriComposer = uriComposer;
        _basketRepository = basketRepository;
        _itemRepository = itemRepository;
    }

    public async Task CreateOrderAsync(int basketId, Address shippingAddress)
    {
        var basketSpec = new BasketWithItemsSpecification(basketId);
        var basket = await _basketRepository.GetBySpecAsync(basketSpec);

        Guard.Against.NullBasket(basketId, basket);
        Guard.Against.EmptyBasketOnCheckout(basket.Items);

        var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
        var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

        var items = basket.Items.Select(basketItem =>
        {
            var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
            var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
            var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
            return orderItem;
        }).ToList();

        var order = new Order(basket.BuyerId, shippingAddress, items);

        await _orderRepository.AddAsync(order);
        
        await this.SendOrder(order);
        await this.PlaceOrderToCosmosDb(order);
    }

    private async Task PlaceOrderToCosmosDb(Order order)
    {
        var orderString = JsonConvert.SerializeObject(order);
        var content = new StringContent(orderString, Encoding.UTF8, "application/json");

        var httpClient = new HttpClient();
        var response = await httpClient.PostAsync(PlaceOrderToCosmosDbUrl, content);
    }

    private async Task SendOrder(Order order)
    {
        var queueClient = new QueueClient(ServiceBusConnectionString, QueueName);

        var orderString = JsonConvert.SerializeObject(order);
        var message = new Message(Encoding.UTF8.GetBytes(orderString));

        await queueClient.SendAsync(message);
    }
}
