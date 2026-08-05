#!/usr/bin/env bash
set -euo pipefail

base_url="${FRONTEND_BASE_URL:-http://127.0.0.1:4173}"
session="parca-muhendisi-ci-${GITHUB_RUN_ID:-local}-$$"
cli=(npx --yes --package "@playwright/cli@0.1.17" playwright-cli)

cleanup() {
  "${cli[@]}" --session "$session" close >/dev/null 2>&1 || true
}
trap cleanup EXIT

"${cli[@]}" --session "$session" open "$base_url"

read -r -d '' smoke_code <<'JS' || true
async (page) => {
  const frontendBaseUrl = page.url().replace(/\/$/, '');
  const consoleErrors = [];
  const pageErrors = [];
  page.on('console', message => {
    if (message.type() === 'error') consoleErrors.push(message.text());
  });
  page.on('pageerror', error => pageErrors.push(error.message));

  const json = body => ({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });

  await page.route('**/api/categories', route => route.fulfill(json([])));
  await page.route('**/api/brands', route => route.fulfill(json([])));
  await page.route('**/api/products/featured', route => route.fulfill(json([])));
  await page.route('**/api/products/101', route => route.fulfill(json({
    id: 101,
    name: 'Browser Test Brake Pad',
    description: 'Verified browser fixture product.',
    partNumber: 'BROWSER-101',
    price: 1250,
    stock: 4,
    imageUrl: '/test-part.png',
    rating: 4.8,
    reviewCount: 12,
    category: { name: 'Brake Parts', slug: 'brake-parts' },
    brand: { name: 'Test Vehicle Brand' },
    partBrand: { name: 'Test Part Brand' },
  })));
  await page.route('**/api/fitment/vehicles/makes', route =>
    route.fulfill(json([{ id: 1, name: 'Test Make' }])));
  await page.route('**/api/fitment/vehicles/models*', route =>
    route.fulfill(json([{ id: 2, name: 'Test Model' }])));
  await page.route('**/api/fitment/vehicles/generations*', route =>
    route.fulfill(json([{ id: 3, name: 'Test Generation', startYear: 2020, endYear: 2024 }])));
  await page.route('**/api/fitment/vehicles/engines*', route =>
    route.fulfill(json([{ id: 4, name: 'Test Engine', engineCode: 'TST' }])));
  await page.route('**/api/fitment/vehicles/configurations*', route =>
    route.fulfill(json([{ id: 5, name: '2020 Test Configuration' }])));
  await page.route('**/api/fitment/products/101*', route =>
    route.fulfill(json({ items: [] })));
  await page.route('**/api/fitment/check*', route => route.fulfill(json({
    match: 'Exact',
    confidence: 0.99,
    confidenceBand: 'VeryHigh',
    isVerified: true,
    message: 'Verified browser fitment',
    sourceName: 'Browser Test Catalog',
  })));
  await page.route('**/api/payments/capabilities', route =>
    route.fulfill(json({ onlineCard: true })));
  await page.route('**/api/legal/checkout-documents', route => route.fulfill(json([
    {
      documentType: 'PreliminaryInformation',
      version: '2026.08',
      title: 'Ön Bilgilendirme Formu',
      content: 'Satıcı, ürün, teslimat ve cayma hakkı bilgileri.',
      contentSha256: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
    },
    {
      documentType: 'DistanceSalesAgreement',
      version: '2026.08',
      title: 'Mesafeli Satış Sözleşmesi',
      content: 'Sipariş ve mesafeli satış koşulları.',
      contentSha256: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
    },
  ])));

  await page.goto(`${frontendBaseUrl}/`);
  await page.getByRole('heading', {
    name: 'Araçlarınız İçin Kaliteli Yedek Parçalar',
  }).waitFor();

  await page.goto(`${frontendBaseUrl}/product/101`);
  await page.getByRole('heading', { name: 'Browser Test Brake Pad' }).waitFor();
  const fitmentSelects = page.locator('.fitment-select-grid select');
  await fitmentSelects.nth(0).selectOption('1');
  await fitmentSelects.nth(1).selectOption('2');
  await fitmentSelects.nth(2).selectOption('3');
  await fitmentSelects.nth(3).selectOption('4');
  await fitmentSelects.nth(4).selectOption('5');
  await page.locator('.fitment-select-grid button').click();
  await page.getByText('Verified browser fitment').waitFor();
  const savedVehicle = JSON.parse(
    (await page.evaluate(() => localStorage.getItem('parca-muhendisi:selected-vehicle'))) || 'null');
  if (savedVehicle?.vehicleId !== 5) {
    throw new Error('Verified vehicle selection was not stored for later product checks.');
  }

  await page.evaluate(() => localStorage.setItem('cart', JSON.stringify([
    { id: 101, name: 'Test Fren Balatası', price: 1250, quantity: 1 },
  ])));
  await page.goto(`${frontendBaseUrl}/checkout`);

  const legalCheckboxes = page.getByRole('checkbox');
  await legalCheckboxes.first().waitFor();
  const submit = page.getByRole('button', { name: /Siparişi Oluştur/ });
  if (!(await submit.isDisabled())) {
    throw new Error('Checkout must stay disabled until every required legal document is accepted.');
  }

  if (await legalCheckboxes.count() !== 2) {
    throw new Error('Checkout must render exactly two required legal acceptances in this fixture.');
  }
  await legalCheckboxes.nth(0).check();
  await legalCheckboxes.nth(1).check();
  if (await submit.isDisabled()) {
    throw new Error('Checkout must unlock after every required legal document is accepted.');
  }

  let capturedOrder = null;
  await page.route('**/api/orders', async route => {
    capturedOrder = route.request().postDataJSON();
    await route.fulfill(json({
      orderNumber: 'PM-E2E-001',
      totalAmount: 1250,
      currency: 'TRY',
      paymentMethod: 'PayAtDelivery',
      paymentStatus: 'Pending',
    }));
  });

  await page.locator('[name=customerName]').fill('Test Kullanıcı');
  await page.locator('[name=customerEmail]').fill('test@example.com');
  await page.locator('[name=customerPhone]').fill('5551112233');
  await page.locator('[name=shippingAddress]').fill('Test Mahallesi No 1');
  await page.locator('[name=city]').fill('İstanbul');
  await page.locator('[name=postalCode]').fill('34000');
  await submit.click();
  await page.getByText('PM-E2E-001').waitFor();

  if (!capturedOrder || capturedOrder.items?.[0]?.productId !== 101) {
    throw new Error('Checkout did not submit the expected server-priced product identity.');
  }
  if (capturedOrder.legalAcceptances?.length !== 2 ||
      capturedOrder.legalAcceptances.some(item =>
        item.accepted !== true || !item.version || item.contentSha256?.length !== 64)) {
    throw new Error('Checkout did not submit the complete versioned legal acceptance evidence.');
  }
  const persistedCart = JSON.parse(
    (await page.evaluate(() => localStorage.getItem('cart'))) || '[]');
  if (persistedCart.length !== 0) {
    throw new Error('Cart must be cleared after a successful checkout response.');
  }

  await page.goto(`${frontendBaseUrl}/admin`);
  await page.waitForURL('**/login');
  if (!page.url().endsWith('/login')) {
    throw new Error('Anonymous admin navigation must redirect to login.');
  }

  await page.setViewportSize({ width: 375, height: 812 });
  await page.goto(`${frontendBaseUrl}/`);
  const hasHorizontalOverflow = await page.evaluate(() =>
    document.documentElement.scrollWidth > document.documentElement.clientWidth + 1);
  if (hasHorizontalOverflow) {
    throw new Error('Mobile home page has horizontal viewport overflow.');
  }

  if (consoleErrors.length || pageErrors.length) {
    throw new Error(`Browser errors: ${JSON.stringify({ consoleErrors, pageErrors })}`);
  }

  return {
    checkout: 'passed',
    fitment: 'passed',
    mobileOverflow: 'passed',
    legalAcceptances: capturedOrder.legalAcceptances.length,
    anonymousAdminRedirect: 'passed',
  };
}
JS

smoke_code_single_line=${smoke_code//$'\r'/}
smoke_code_single_line=${smoke_code_single_line//$'\n'/ }

SMOKE_CODE="$smoke_code_single_line" node -e \
  'new Function(`return (${process.env.SMOKE_CODE})`);'

result=$(FRONTEND_BASE_URL="$base_url" \
  "${cli[@]}" --session "$session" run-code "$smoke_code_single_line")
printf '%s\n' "$result"
grep -q '"checkout":"passed"' <<<"$result"
