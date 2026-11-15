// Renders File Explorer's tile, icon and splash PNGs from Younes's art in ../art (needs Playwright's
// Chromium):  node render-art.js
//   file-explorer-tile.svg    dark folder with a padlock on yellow: store logo and splash screen
//   file-explorer-glyph.svg   the same glyph in white on transparent: tiles and app icon take the
//                             user's accent color
// No wide tile until there's a wide design.
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const art = path.join(__dirname, '..', 'art');
const out = path.join(__dirname, '..', 'RetroFiles', 'Assets');
const YELLOW = 'rgb(255,232,59)';
const svg = name => 'data:image/svg+xml;base64,' + fs.readFileSync(path.join(art, name)).toString('base64');

const jobs = [
  // [file, width, height, svg, background]
  ['ApplicationIcon.png', 100, 100, 'file-explorer-glyph.svg', null],
  ['Tiles/FlipCycleTileSmall.png', 159, 159, 'file-explorer-glyph.svg', null],
  ['Tiles/FlipCycleTileMedium.png', 336, 336, 'file-explorer-glyph.svg', null],
  ['Square150x150Logo.png', 150, 150, 'file-explorer-glyph.svg', null],
  ['Square71x71Logo.png', 71, 71, 'file-explorer-glyph.svg', null],
  ['Square44x44Logo.png', 44, 44, 'file-explorer-glyph.svg', null],
  ['StoreLogo.png', 50, 50, 'file-explorer-tile.svg', YELLOW],
  ['SplashScreen.png', 480, 800, 'file-explorer-tile.svg', YELLOW],
  // scale-240 copies for the 8.1 package (the OS picks them on high-resolution phones)
  ['Square150x150Logo.scale-240.png', 360, 360, 'file-explorer-glyph.svg', null],
  ['Square71x71Logo.scale-240.png', 170, 170, 'file-explorer-glyph.svg', null],
  ['Square44x44Logo.scale-240.png', 106, 106, 'file-explorer-glyph.svg', null],
  ['StoreLogo.scale-240.png', 120, 120, 'file-explorer-tile.svg', YELLOW],
  ['SplashScreen.scale-240.png', 1152, 1920, 'file-explorer-tile.svg', YELLOW],
  // the splash Silverlight apps actually show: SplashScreenImage.jpg in the project root
  ['../SplashScreenImage.jpg', 768, 1280, 'file-explorer-tile.svg', YELLOW],
];

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  for (const [file, w, h, src, bg, scale] of jobs) {
    await page.setViewportSize({ width: w, height: h });
    const side = Math.round(Math.min(w, h) * (scale || 1));
    await page.setContent(`<html><body style="margin:0;width:${w}px;height:${h}px;background:${bg || 'transparent'};
      display:flex;align-items:center;justify-content:center">
      <img src="${svg(src)}" style="width:${side}px;height:${side}px"></body></html>`);
    await page.waitForFunction(() => document.images[0].complete);
    await page.screenshot(file.endsWith('.jpg') ? { path: path.join(out, file), type: 'jpeg', quality: 92 }
                                               : { path: path.join(out, file), omitBackground: !bg });
    console.log(file, w + 'x' + h);
  }
  await browser.close();
})();
