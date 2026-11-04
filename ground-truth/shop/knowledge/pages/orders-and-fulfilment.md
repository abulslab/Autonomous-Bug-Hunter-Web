# Orders and fulfilment

## Placing an order
- A shopper can only buy what is in stock, and at least one of each product.
- Checkout needs a name, a street address, a city, a five-digit ZIP code and a valid card: the number passes the Luhn
  check, the expiry (MM/YY) is not in the past, and the CVV has three or four digits.

## Order status
- A new order is **Processing**. Staff mark it **Shipped**, then **Delivered**, in that order.
- A shopper can cancel their own order only while it is Processing; the items go back into stock and the order shows
  as **Cancelled**.
- Only staff can see other shoppers' orders or change an order's status.

## Test data
The shop starts with one delivered order (ORD-1001) for the demo shopper.
