// Renders File Explorer's tile, icon and splash PNGs from Younes's art in ../art (needs Playwright's
// Chromium):  node render-art.js
//   file-explorer-tile.svg   dark folder with a padlock on yellow, used as is everywhere
// No wide tile until there's a wide design.
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const art = path.join(__dirname, '..', 'art');
const out = path.join(__dirname, '..', 'RetroFiles', 'Assets');
const YELLOW = 'rgb(255,232,59)';
const svg = name => 'data:image/svg+xml;base64,' + fs.readFileSync(path.join(art, name)).toString('base64');

const jobs = [
  // [file, width, height]
  ['ApplicationIcon.png', 100, 100],
  ['Tiles/FlipCycleTileSmall.png', 159, 159],
  ['Tiles/FlipCycleTileMedium.png', 336, 336],
  ['Square150x150Logo.png', 150, 150],
  ['Square71x71Logo.png', 71, 71],
  ['Square44x44Logo.png', 44, 44],
  ['StoreLogo.png', 50, 50],
  ['SplashScreen.png', 480, 800],
];

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  for (const [file, w, h] of jobs) {
    await page.setViewportSize({ width: w, height: h });
    const side = Math.min(w, h);
    await page.setContent(`<html><body style="margin:0;width:${w}px;height:${h}px;background:${YELLOW};
      display:flex;align-items:center;justify-content:center">
      <img src="${svg('file-explorer-tile.svg')}" style="width:${side}px;height:${side}px"></body></html>`);
    await page.waitForFunction(() => document.images[0].complete);
    await page.screenshot({ path: path.join(out, file) });
    console.log(file, w + 'x' + h);
  }
  await browser.close();
})();
