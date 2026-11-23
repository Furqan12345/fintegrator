const { chromium } = require('C:/Users/FurqanIrfan/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const path = require('path');

(async () => {
  const password = process.env.FINTEGRATOR_GATEWAY_PASSWORD;
  if (!password) throw new Error('FINTEGRATOR_GATEWAY_PASSWORD is required');
  const browser = await chromium.launch({ headless: true, executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
  try {
    const context = await browser.newContext({ httpCredentials: { username: 'admin', password } });
    const page = await context.newPage();
    const failures = [];
    const frameworkResponses = [];
    const startedAt = Date.now();
    page.on('console', message => { if (message.type() === 'error') failures.push('console: ' + message.text()); });
    page.on('requestfailed', request => failures.push('request failed: ' + new URL(request.url()).pathname + ' ' + request.failure()?.errorText));
    page.on('response', response => {
      if (new URL(response.url()).pathname.startsWith('/_framework/')) frameworkResponses.push(`${response.status()} ${new URL(response.url()).pathname}`);
      if (response.status() >= 400) failures.push(`${response.status()} ${new URL(response.url()).pathname}`);
    });
    page.on('pageerror', error => failures.push(`page error: ${error.message}`));
    const response = await page.goto(process.env.FINTEGRATOR_URL || 'https://fintegrator-web-e07356e8.azurewebsites.net/', { waitUntil: 'domcontentloaded', timeout: 60000 });
    let loaded = false;
    try {
      await page.locator('#app-splash').waitFor({ state: 'detached', timeout: 120000 });
      loaded = true;
    } catch {}
    const title = await page.title();
    const heading = await page.locator('h1').first().textContent().catch(() => null);
    await page.screenshot({ path: path.resolve('artifacts/appservice/browser-verification.png'), fullPage: true });
    console.log(JSON.stringify({ status: response.status(), loaded, title, heading, elapsedSeconds: Math.round((Date.now() - startedAt) / 1000), frameworkCount: frameworkResponses.length, lastFrameworkResponses: frameworkResponses.slice(-12), failures: failures.slice(0, 20) }));
    if (!loaded || failures.length) process.exitCode = 1;
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
