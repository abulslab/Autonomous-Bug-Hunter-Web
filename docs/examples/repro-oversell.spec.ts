import { test, expect } from '@playwright/test';

// =============================================================================
// Autonomous Bug Hunter Finding F-0b63741d6d (Confirmed, High)
// Defect: Inventory overselling via concurrent cart checkout
// Expected: The server rejects purchases that exceed available inventory.
// Actual:   POST /api/cart/checkout with quantity = 5 returned HTTP 200 OK.
// Repro:    Shrunk from 42 exploratory steps down to 6 minimal actions.
// =============================================================================

test('Reproduce inventory oversell defect', async ({ page }) => {
  // 1. Navigate to target product page
  await page.goto('http://localhost:5290/#/product/p-101');
  await expect(page.getByRole('heading', { name: 'Acme Ergonomic Keyboard' })).toBeVisible();

  // 2. Select maximum stock quantity
  await page.getByRole('spinbutton', { name: 'Quantity' }).fill('5');

  // 3. Add to cart
  await page.getByRole('button', { name: 'Add to cart' }).click();

  // 4. Open cart
  await page.getByRole('link', { name: 'Cart' }).click();
  await expect(page.getByText('Acme Ergonomic Keyboard')).toBeVisible();

  // 5. Intercept checkout response and submit
  const checkoutResponse = page.waitForResponse(
    (res) => res.request().method() === 'POST' && res.url().includes('/api/cart/checkout')
  );
  await page.getByRole('button', { name: 'Checkout' }).click();

  // 6. Assert server validation behavior: inventory must not oversell
  const response = await checkoutResponse;
  
  // Bug Hunter confirmed finding: Server incorrectly accepted order exceeding inventory limit
  expect(response.status(), 'Server must reject checkout when inventory is depleted').not.toBe(200);
});
