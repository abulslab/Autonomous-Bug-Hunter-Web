---
name: bookings
title: Reservations and bookings
match: booking, reservation, check-in, checkout date, stay, night, guests, room, availability, cancel
---

## Risks
- Date order: the end of a stay or reservation comes after its start; a stay is at least one night.
- Past dates: a new reservation cannot start before today (in the property's time zone).
- Capacity: the number of guests is at least 1 and no more than the room or slot allows.
- Price: total = nights x nightly rate, before any stated discount, fee or tax; every night is charged.
- Availability: two confirmed bookings of the same room never overlap.
- Cancellation: a cancelled booking stops counting against availability and shows as cancelled everywhere.
- Consistency: dates, guests and total are the same on the form, the confirmation and the booking list.

## Rules
- On booking writes: `status >= 400 || nights >= 1`
- On booking writes: `status >= 400 || total == nights * rate`
- On booking writes: `status >= 400 || guests <= maxGuests`

## Probe values
- Dates: yesterday, check-out equal to check-in, check-out before check-in, a date a year ahead, 2025-02-30
- Guests: 0, -1, the room maximum + 1
- Stay length: 1, 3 and 14 nights (pricing bugs often appear only on longer stays)
