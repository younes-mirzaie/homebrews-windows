// Renders the Windows 10 app's tile, icon and splash PNGs from the same art as the phone app
// (../art), into ../../app-uwp/Retrobird10/Assets (needs Playwright's Chromium):
//   node render-art-uwp.js
// Tiles are transparent (they take the user's accent color); the system writes the name under
// the bird, so the bird sits a little smaller than on the phone's tiles.
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const art = path.join(__dirname, '..', 'art');
const out = path.join(__dirname, '..', '..', 'app-uwp', 'Retrobird10', 'Assets');
const svg = name => 'data:image/svg+xml;base64,' + fs.readFileSync(path.join(art, name)).toString('base64');

const jobs = [];   // [file, width, height, svg, glyph size as a share of the height]
const scales = [[100, 1], [200, 2], [400, 4]];
for (const [s, k] of scales) {
  jobs.push([`Square150x150Logo.scale-${s}.png`, 150 * k, 150 * k, 'retrobird-tile.svg', 0.66]);
  jobs.push([`Wide310x150Logo.scale-${s}.png`, 310 * k, 150 * k, 'retrobird-tile.svg', 0.66]);
  jobs.push([`Square71x71Logo.scale-${s}.png`, 71 * k, 71 * k, 'retrobird-tile.svg', 0.9]);
  jobs.push([`Square310x310Logo.scale-${s}.png`, 310 * k, 310 * k, 'retrobird-tile.svg', 0.5]);
  jobs.push([`Square44x44Logo.scale-${s}.png`, 44 * k, 44 * k, 'retrobird-tile.svg', 1]);
  jobs.push([`BadgeLogo.scale-${s}.png`, 24 * k, 24 * k, 'retrobird-tile.svg', 1]);
  jobs.push([`StoreLogo.scale-${s}.png`, 50 * k, 50 * k, 'retrobird-store.svg', 1]);
  jobs.push([`SplashScreen.scale-${s}.png`, 620 * k, 300 * k, 'retrobird-tile.svg', 0.6]);
}
// the app list and taskbar icon at exact sizes, plated (accent) and unplated
for (const t of [16, 24, 32, 48, 256]) {
  jobs.push([`Square44x44Logo.targetsize-${t}.png`, t, t, 'retrobird-tile.svg', 1]);
  jobs.push([`Square44x44Logo.targetsize-${t}_altform-unplated.png`, t, t, 'retrobird-store.svg', 1]);
}

(async () => {
  fs.mkdirSync(out, { recursive: true });
  const browser = await chromium.launch();
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  for (const [file, w, h, src, share] of jobs) {
    await page.setViewportSize({ width: w, height: h });
    const side = Math.round(Math.min(w, h) * share);
    await page.setContent(`<html><body style="margin:0;width:${w}px;height:${h}px;background:transparent;
      display:flex;align-items:center;justify-content:center">
      <img src="${svg(src)}" style="width:${side}px;height:${side}px"></body></html>`);
    await page.waitForFunction(() => document.images[0].complete);
    await page.screenshot({ path: path.join(out, file), omitBackground: true });
    console.log(file, w + 'x' + h);
  }
  await browser.close();
})();
