namespace DemoShop;

public record Story(string Key, string Title, string Criteria);

public static class Stories
{
    public static readonly Story[] All =
    [
        new("DEMO-121", "Cart totals are always correct",
            "A line total is the price times the quantity.\nThe subtotal is the sum of the line totals.\nA discount never exceeds the subtotal.\nTax is 8% of (subtotal - discount), rounded to the nearest cent.\nThe total is subtotal - discount + shipping + tax."),
        new("DEMO-122", "Each coupon applies once",
            "Applying a coupon that is already in the cart is rejected or leaves the discount unchanged.\nThe cart never lists the same coupon twice.\nAn unknown coupon code is rejected with a message."),
        new("DEMO-123", "Quantities and stock are respected",
            "A quantity below 1 is rejected.\nA shopper cannot add more of a product than is in stock."),
        new("DEMO-124", "Search ignores letter case",
            "Searching returns every product whose name contains the term, ignoring letter case."),
        new("DEMO-125", "Checkout validates the payment card",
            "A card whose expiry date (MM/YY) is in the past is rejected.\nA card number that fails the Luhn check is rejected."),
        new("DEMO-126", "Orders can be cancelled until they ship",
            "A shopper can cancel their own order while it is Processing.\nAn order that is Shipped or Delivered cannot be cancelled.\nCancelling returns the items to stock."),
        new("DEMO-127", "Staff manage orders",
            "Only staff can see all orders and change an order's status.\nAn order goes from Processing to Shipped to Delivered, in that order."),
        new("DEMO-128", "The listed price is the price charged",
            "A product's price in the cart is the price shown for it in the product list."),
    ];
}
