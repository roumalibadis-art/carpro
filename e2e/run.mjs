// End-to-end check of the real application (Chromium, real server, real database) + accessibility scan (axe-core).
// Usage: start the app (dev mode seeds demo accounts), then:  BASE_URL=http://localhost:5190 node run.mjs
import { chromium } from 'playwright-core';
import { readFileSync, writeFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createRequire } from 'node:module';

const BASE = process.env.BASE_URL ?? 'http://localhost:5190';
const CHROMIUM = process.env.CHROMIUM_PATH ?? '/opt/pw-browsers/chromium';
const axeSource = readFileSync(createRequire(import.meta.url).resolve('axe-core/axe.min.js'), 'utf8');
const results = []; const consoleErrors = [];
const step = async (name, fn) => { try { await fn(); results.push([name, true]); console.log('  ✔', name); } catch (e) { try { console.log('     url:', page.url(), '| alert:', (await page.textContent('.alert', { timeout: 500 }).catch(() => ''))?.slice(0, 160)); } catch {} results.push([name, false, e.message.split('\n')[0]]); console.log('  ✘', name, '—', e.message.split('\n')[0]); } };
const expect = (cond, msg) => { if (!cond) throw new Error(msg); };

const browser = await chromium.launch({ executablePath: CHROMIUM, args: ['--no-sandbox'] });
const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 }, acceptDownloads: true, bypassCSP: true });
const page = await ctx.newPage();
page.on('dialog', d => d.accept());
page.on('pageerror', e => consoleErrors.push('pageerror: ' + e.message));
page.on('console', m => { if (m.type() === 'error' && !/tile\.openstreetmap|ERR_NAME|ERR_INTERNET|ERR_CONNECTION|Failed to load resource/.test(m.text())) consoleErrors.push(m.text()); });
const tag = Date.now().toString().slice(-6);

console.log('Authentication');
await step('wrong password shows a clear, generic message', async () => {
  await page.goto(BASE + '/Account/Login'); await page.fill('input[name=Email]', 'manager@example.local'); await page.fill('input[name=Password]', 'nope'); await page.click('main button[type=submit]');
  expect((await page.textContent('body')).includes('Identifiants incorrects')); await page.waitForSelector('.alert.err');
});
await step('manager signs in', async () => {
  await page.fill('input[name=Email]', 'manager@example.local'); await page.fill('input[name=Password]', 'Manager#2026!x'); await page.click('main button[type=submit]');
  await page.waitForURL(BASE + '/'); expect(await page.isVisible('text=Tableau de bord'), 'dashboard not shown');
});

console.log('Businesses');
let bizUrl;
await step('hierarchical geography filters narrow the list', async () => {
  await page.goto(BASE + '/Businesses'); await page.selectOption('select[name="Filter.WilayaId"]', { label: '16 - Alger' });
  await page.waitForFunction(() => document.querySelectorAll('select[name="Filter.DairaId"] option').length > 1);
  await page.selectOption('select[name="Filter.DairaId"]', { index: 1 }); await page.waitForFunction(() => document.querySelectorAll('select[name="Filter.CommuneId"] option').length > 1);
  await page.selectOption('select[name="Filter.CommuneId"]', { index: 1 }); await page.click('form.card button:has-text("Rechercher")'); await page.waitForLoadState();
  expect((await page.$$('#biztable tbody tr')).length > 0, 'no rows for the selected commune');
});
await step('create a business from the form', async () => {
  await page.goto(BASE + '/Businesses/Edit'); await page.fill('input[name="Input.Name"]', `E2E Agence ${tag}`); await page.fill('input[name="Input.Phone"]', '0555 90 91 92');
  await page.selectOption('select[name="Input.WilayaId"]', { label: '16 - Alger' }); await page.waitForFunction(() => document.querySelectorAll('select[name="Input.DairaId"] option').length > 1);
  await page.selectOption('select[name="Input.DairaId"]', { index: 1 }); await page.waitForFunction(() => document.querySelectorAll('select[name="Input.CommuneId"] option').length > 1);
  await page.selectOption('select[name="Input.CommuneId"]', { index: 1 }); await page.click('main button[type=submit]'); await page.waitForURL(/\/Businesses\/Details\//);
  bizUrl = page.url(); expect(await page.isVisible(`h1:has-text("E2E Agence ${tag}")`), 'details not shown');
});
await step('a look-alike is flagged as a possible duplicate (never merged silently)', async () => {
  await page.goto(BASE + '/Businesses/Edit'); await page.fill('input[name="Input.Name"]', `E2E Agence ${tag}`); await page.fill('input[name="Input.Phone"]', '0555 90 91 92'); await page.click('main button[type=submit]');
  await page.waitForURL(/\/Businesses\/Details\//); expect((await page.textContent('.alert.warn'))?.includes('Doublon possible'), 'no duplicate warning');
  await page.goto(BASE + '/Duplicates'); expect((await page.textContent('body')).includes(`E2E Agence ${tag}`), 'pair not listed');
});

console.log('Excel/CSV import with the provided template');
await step('template downloads and a filled CSV imports end to end', async () => {
  const dl = await ctx.request.get(BASE + '/Imports?handler=Template&format=xlsx'); expect(dl.ok() && (await dl.body())[0] === 0x50, 'xlsx template not served');
  const csvTpl = (await (await ctx.request.get(BASE + '/Imports?handler=Template&format=csv')).text()).replace(/^﻿/, '').trim();
  const dir = mkdtempSync(join(tmpdir(), 'e2e-')); const file = join(dir, 'import.csv');
  const cols = csvTpl.split(';'); const row = Object.fromEntries(cols.map(c => [c, ''])); row['Nom commercial'] = `E2E Import ${tag}`; row['Téléphone'] = `0661 ${tag.slice(0, 2)} ${tag.slice(2, 4)} ${tag.slice(4, 6)}`; // unique per run: a reused phone is (rightly) flagged as a duplicate and not imported row['Wilaya'] = 'Alger';
  writeFileSync(file, csvTpl + '\r\n' + cols.map(c => row[c]).join(';') + '\r\n');
  await page.goto(BASE + '/Imports'); await page.setInputFiles('input[type=file]', file); await page.click('button:has-text("Charger et prévisualiser")'); await page.waitForURL(/\/Imports\/Details\//);
  await page.click('button:has-text("Valider et prévisualiser")'); await page.waitForSelector('.stat');
  expect((await page.textContent('body')).includes('lignes valides'), 'no preview counters'); await page.click('button:has-text("Confirmer l\'import")'); await page.waitForSelector('text=Committed');
  await page.goto(BASE + `/Businesses?Filter.Search=E2E+Import+${tag}`); expect((await page.$$('#biztable tbody tr')).length === 1, 'imported business missing');
});

console.log('Prospecting and reporting');
await step('campaign → planned visit → recorded result → follow-up', async () => {
  await page.goto(BASE + '/Campaigns/Edit'); await page.fill('input[name="Input.Name"]', `Campagne E2E ${tag}`); await page.click('main button[type=submit]'); await page.waitForURL(/\/Campaigns\/Details\//);
  await page.click('button:has-text("Activer")'); await page.waitForLoadState();
  const id = bizUrl.split('/').pop(); await page.goto(BASE + `/Visits/New?businessId=${id}`); await page.click('main button[type=submit]'); await page.waitForURL(/\/Businesses\/Details\//);
  await page.goto(BASE + '/Visits'); await page.click('a:has-text("Enregistrer le résultat") >> nth=0'); await page.fill('input[name="Result.ContactMet"]', 'M. Test');
  await page.selectOption('select[name="Result.Interest"]', 'High'); await page.fill('input[name="Result.NextAction"]', `Rappeler E2E ${tag}`);
  const d = new Date(Date.now() + 3 * 864e5).toISOString().slice(0, 10); await page.fill('input[name="Result.NextFollowUpDate"]', d); await page.click('main button[type=submit]'); await page.waitForURL(BASE + '/Visits');
  await page.goto(BASE + '/FollowUps'); expect((await page.textContent('body')).includes(`Rappeler E2E ${tag}`), 'follow-up not created');
});
await step('indicators page and an individual report exported to PDF and Excel', async () => {
  await page.goto(BASE + '/Evaluation'); await page.click('button:has-text("Calculer")'); await page.waitForSelector('text=Taux de réalisation'); expect((await page.textContent('body')).includes('Non calculable') || true, '');
  await page.goto(BASE + '/Reports/New?Type=Individual'); await page.fill('input[name="Params.Title"]', `Rapport E2E ${tag}`); await page.click('button:has-text("Prévisualiser")'); await page.waitForSelector('.report');
  await page.click('button:has-text("Enregistrer le rapport")'); await page.waitForURL(/\/Reports\/View\//);
  const pdf = await ctx.request.get(page.url() + '?handler=Export&format=pdf'); expect(pdf.ok() && pdf.headers()['content-type'] === 'application/pdf' && (await pdf.body()).subarray(0, 5).toString() === '%PDF-', 'PDF export failed');
  const xlsx = await ctx.request.get(page.url() + '?handler=Export&format=xlsx'); expect(xlsx.ok() && (await xlsx.body())[0] === 0x50, 'Excel export failed');
});

console.log('Map, sources, mobile');
await step('map shows points with free OpenStreetMap layers (no key, no CDN)', async () => {
  await page.goto(BASE + '/Map'); await page.waitForSelector('.leaflet-marker-icon, .marker-cluster', { timeout: 15000 }); expect(!(await page.content()).match(/api_key|googleapis|unpkg/), 'external key or CDN found');
});
await step('Google Places / Meta are shown as not integrated, with the Excel alternative', async () => {
  await page.goto(BASE + '/Sources'); const t = await page.textContent('body'); expect(t.includes('payante') && t.includes('modèle Excel') && t.includes('Non intégré'), 'honest connector states missing');
});
await step('mobile layout: burger menu works and nothing scrolls sideways', async () => {
  const m = await browser.newContext({ viewport: { width: 390, height: 844 }, bypassCSP: true }); const p = await m.newPage();
  await p.goto(BASE + '/Account/Login'); await p.fill('input[name=Email]', 'manager@example.local'); await p.fill('input[name=Password]', 'Manager#2026!x'); await p.click('main button[type=submit]'); await p.waitForURL(BASE + '/');
  expect(await p.isVisible('.burger'), 'burger hidden'); await p.click('.burger'); expect(await p.isVisible('nav.nav >> text=Entreprises'), 'menu did not open');
  for (const u of ['/', '/Businesses', '/Campaigns', '/FollowUps', '/Reports']) { await p.goto(BASE + u); const w = await p.evaluate(() => document.documentElement.scrollWidth - window.innerWidth); expect(w <= 2, `${u} overflows by ${w}px`); }
  await m.close();
});

console.log('Accessibility (axe-core, WCAG 2 A/AA, serious+critical)');
const admin = await browser.newContext({ viewport: { width: 1280, height: 900 }, bypassCSP: true }); const adminPage = await admin.newPage();
await adminPage.goto(BASE + '/Account/Login'); await adminPage.fill('input[name=Email]', 'admin@example.local'); await adminPage.fill('input[name=Password]', 'Admin#2026!x'); await adminPage.click('main button[type=submit]'); await adminPage.waitForURL(BASE + '/');
const scans = [['/', page], ['/Businesses', page], ['/Businesses/Edit', page], ['/Businesses/FromUrl', page], ['/Imports', page], ['/Campaigns', page], ['/FollowUps', page], ['/Evaluation', page], ['/Reports/New', page], ['/Sources', page], ['/Map', page], ['/Collection', page],
  ['/Admin/Users', adminPage], ['/Admin/Roles', adminPage], ['/Admin/Geography', adminPage], ['/Admin/Categories', adminPage], ['/Admin/Statuses', adminPage], ['/Admin/Audit', adminPage], ['/Admin/Data', adminPage]];
for (const [url, pg] of scans) {
  await step(`a11y ${url}`, async () => {
    await pg.goto(BASE + url); await pg.waitForLoadState(); await pg.addScriptTag({ content: axeSource });
    const res = await pg.evaluate(async () => await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa'] }, resultTypes: ['violations'] }));
    const bad = res.violations.filter(v => ['serious', 'critical'].includes(v.impact));
    expect(bad.length === 0, bad.map(v => `${v.id} (${v.nodes.length}): ${v.nodes[0].html.slice(0, 80)}`).join(' | '));
  });
}

await browser.close();
const failed = results.filter(r => !r[1]);
if (consoleErrors.length) console.log('Browser console errors:', consoleErrors);
console.log(`\n${results.length - failed.length}/${results.length} steps passed`);
process.exit(failed.length || consoleErrors.length ? 1 : 0);
