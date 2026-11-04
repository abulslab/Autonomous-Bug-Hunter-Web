---
name: ecommerce
title: E-commerce cart and checkout
match: cart, checkout, basket, coupon, promo, order, product, price, shipping, tax, stock
---

## Risks
- Money arithmetic: line total = price x quantity; subtotal = sum of lines; total = subtotal - discount + shipping + tax.
- Tax rounding: tax should round to the nearest cent, not truncate.
- Discounts: a discount never exceeds the subtotal; the same coupon applies at most once; unknown or expired codes are rejected.
- Quantities: below 1 is rejected or removes the line; non-integer quantities are rejected, not a server error.
- Inventory: a cart or order cannot exceed the stock shown ("Only N left").
- Checkout validation: expired cards (MM/YY before today) and card numbers failing the Luhn check are rejected.
- Consistency: the price on the listing, product page, cart, checkout and order confirmation is the same.
- Order history: a placed order appears with the same lines and total as the confirmation.

## Rules
- On cart and order responses: `total == subtotal - discount + shipping + tax`
- On cart and order responses: `discount <= subtotal`
- On cart responses: `all(items, lineTotal == price * qty)`
- On cart item writes: `status >= 400 || request.qty >= 1`

## Probe values
- Quantity: -1, 0, 0.5, stock + 1, 99999
- Coupon: the same code twice, an unknown code, a lower-case variant of a valid code
- Card expiry: 01/20; card number: a valid one with its last digit changed
