// Renders Retrobird's tile, icon and splash PNGs from Younes's art in ../art (needs Playwright's
// Chromium):  node render-art.js
//   retrobird-tile.svg   white glyph on transparent: tiles and app icon take the accent color
//   retrobird-store.svg  the same glyph on blue: store logo and splash screen
// BadgeLogo: the white glyph alone, for the unread count on the lock screen.
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const art = path.join(__dirname, '..', 'art');
const out = path.join(__dirname, '..', 'Retrobird', 'Assets');
const BLUE = 'rgb(64,170,227)';
const svg = name => 'data:image/svg+xml;base64,' + fs.readFileSync(path.join(art, name)).toString('base64');

// Windows Phone 8.1 (WinRT) assets at scale-240, the largest the phone uses; it scales down.
const jobs = [
  // [file, width, height, svg, background]
  ['Square150x150Logo.scale-240.png', 360, 360, 'retrobird-tile.svg', null],
  ['Square71x71Logo.scale-240.png', 170, 170, 'retrobird-tile.svg', null],
  ['Square44x44Logo.scale-240.png', 106, 106, 'retrobird-tile.svg', null],
  ['BadgeLogo.scale-240.png', 58, 58, 'retrobird-tile.svg', null],
  // wide: the glyph at the same size as on the medium tile, centered
  ['Wide310x150Logo.scale-240.png', 744, 360, 'retrobird-tile.svg', null, 360],
  ['StoreLogo.scale-240.png', 120, 120, 'retrobird-store.svg', null],
  ['SplashScreen.scale-240.png', 1152, 1920, 'retrobird-store.svg', BLUE],
];

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  for (const [file, w, h, src, bg, glyph] of jobs) {
    await page.setViewportSize({ width: w, height: h });
    const side = glyph || Math.min(w, h);
    await page.setContent(`<html><body style="margin:0;width:${w}px;height:${h}px;background:${bg || 'transparent'};
      display:flex;align-items:center;justify-content:center">
      <img src="${svg(src)}" style="width:${side}px;height:${side}px"></body></html>`);
    await page.waitForFunction(() => document.images[0].complete);
    await page.screenshot({ path: path.join(out, file), omitBackground: !bg });
    console.log(file, w + 'x' + h);
  }
  await browser.close();
})();
