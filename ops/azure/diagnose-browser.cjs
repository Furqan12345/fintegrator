const { chromium } = require('C:/Users/FurqanIrfan/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
(async () => {
  const browser = await chromium.launch({ headless: true, executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
  try {
    const page = await (await browser.newContext({ httpCredentials: { username: 'admin', password: process.env.FINTEGRATOR_GATEWAY_PASSWORD } })).newPage();
    const started = new Map(), api = [], errors = [];
    page.on('request', r => started.set(r, Date.now()));
    page.on('response', r => { const u = new URL(r.url()); if (u.pathname.startsWith('/api/')) api.push({ path: u.pathname + u.search, status: r.status(), ms: Date.now() - (started.get(r.request()) ?? Date.now()) }); if (r.status() >= 400) errors.push(`${r.status()} ${u.pathname}`); });
    page.on('pageerror', e => errors.push(e.message));
    await page.goto('https://fintegrator-web-e07356e8.azurewebsites.net/', { waitUntil: 'domcontentloaded', timeout: 60000 });
    await page.getByRole('heading', { name: 'Integration overview' }).waitFor({ state: 'visible', timeout: 120000 });
    await page.waitForTimeout(15000);
    const state = await page.evaluate(() => ({ title: document.title, heading: document.querySelector('h2')?.textContent, loading: [...document.querySelectorAll('.loading-state')].map(e => ({ text: e.textContent, visible: !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length) })), splash: !!document.querySelector('#app-splash'), bodyEnd: document.body.innerText.slice(-250) }));
    await page.screenshot({ path: 'artifacts/appservice/live-browser-final.png', fullPage: true });
    console.log(JSON.stringify({ state, api, errors }));
    if (state.loading.some(x => x.visible) || errors.length) process.exitCode = 1;
  } finally { await browser.close(); }
})().catch(e => { console.error(e.message); process.exitCode = 1; });
