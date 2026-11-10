// Renders Retrobird's tile, icon and splash PNGs from Younes's art in ../art (needs Playwright's
// Chromium):  node render-art.js
//   retrobird-tile.svg   white glyph on transparent: tiles and app icon take the accent color
//   retrobird-store.svg  the same glyph on blue: store logo and splash screen
// No wide tile until there's a wide design.
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const art = path.join(__dirname, '..', 'art');
const out = path.join(__dirname, '..', 'Retrobird', 'Assets');
const BLUE = 'rgb(64,170,227)';
const svg = name => 'data:image/svg+xml;base64,' + fs.readFileSync(path.join(art, name)).toString('base64');

const jobs = [
  // [file, width, height, svg, background]
  ['ApplicationIcon.png', 100, 100, 'retrobird-tile.svg', null],
  ['Tiles/FlipCycleTileSmall.png', 159, 159, 'retrobird-tile.svg', null],
  ['Tiles/FlipCycleTileMedium.png', 336, 336, 'retrobird-tile.svg', null],
  ['Square150x150Logo.png', 150, 150, 'retrobird-tile.svg', null],
  ['Square71x71Logo.png', 71, 71, 'retrobird-tile.svg', null],
  ['Square44x44Logo.png', 44, 44, 'retrobird-tile.svg', null],
  ['StoreLogo.png', 50, 50, 'retrobird-store.svg', null],
  ['SplashScreen.png', 480, 800, 'retrobird-store.svg', BLUE],
];

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  for (const [file, w, h, src, bg] of jobs) {
    await page.setViewportSize({ width: w, height: h });
    const side = Math.min(w, h);
    await page.setContent(`<html><body style="margin:0;width:${w}px;height:${h}px;background:${bg || 'transparent'};
      display:flex;align-items:center;justify-content:center">
      <img src="${svg(src)}" style="width:${side}px;height:${side}px"></body></html>`);
    await page.waitForFunction(() => document.images[0].complete);
    await page.screenshot({ path: path.join(out, file), omitBackground: !bg });
    console.log(file, w + 'x' + h);
  }
  await browser.close();
})();
