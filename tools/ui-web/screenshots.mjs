// Screenshot harness: every page at 1280 and 390 wide, in each mock scenario, plus an overflow check.
//   bun mock-server.ts --port 5181 &   then   node screenshots.mjs
// PLAYWRIGHT_CORE points at any playwright-core install; CHROMIUM at a chromium binary.
import { createRequire } from "module";
import { mkdirSync } from "fs";
import { join, dirname } from "path";
import { fileURLToPath } from "url";

const here = dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_CORE ?? "/home/filip/work/lrb-vision/node_modules/playwright-core");
const BASE = process.env.BASE ?? "http://localhost:5181/";
const OUT = join(here, "screenshots");
mkdirSync(OUT, { recursive: true });

const scenario = (body) =>
  fetch(new URL("mock/scenario", BASE), { method: "POST", body: JSON.stringify(body) }).then((r) => r.json());

const browser = await chromium.launch({ executablePath: process.env.CHROMIUM ?? "/usr/bin/chromium" });
const contexts = {
  1280: await browser.newContext({ viewport: { width: 1280, height: 800 } }),
  390: await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 }),
};
const failures = [];

async function shot(name, hash, { width, wait = 1800, before, setup, fullPage = true } = {}) {
  for (const w of width ? [width] : [1280, 390]) {
    if (setup) await setup();
    const page = await contexts[w].newPage();
    await page.goto(new URL(hash, BASE).href);
    await page.waitForTimeout(wait);
    if (before) await before(page);
    const { sw, cw } = await page.evaluate(() => ({
      sw: document.documentElement.scrollWidth,
      cw: document.documentElement.clientWidth,
    }));
    const file = join(OUT, `${name}-${w}.png`);
    await page.screenshot({ path: file, fullPage });
    const ok = sw === cw;
    if (!ok) failures.push(`${name}-${w}: scrollWidth ${sw} != clientWidth ${cw}`);
    console.log(`${ok ? "ok  " : "FAIL"} ${name}-${w}.png  scrollWidth=${sw} clientWidth=${cw}`);
    await page.close();
  }
}

await scenario({ sources: 0, bridges: "none", offline: false });
await shot("sources-empty", "#/sources");
await shot("watch-empty", "#/watch");
await shot("bridges-empty", "#/bridges");

await scenario({ sources: 1, bridges: "none" });
await shot("sources-one", "#/sources");
await shot("watch-one", "#/watch/s1", { wait: 2500 });

await scenario({ sources: 12, bridges: "none" });
await shot("sources-many", "#/sources", { wait: 2500 });
await shot("sources-many-hover", "#/sources", {
  width: 1280, wait: 2500, fullPage: false, before: async (p) => { await p.hover(".tile:nth-child(2)"); await p.waitForTimeout(400); },
});
await shot("sources-filter-omtx", "#/sources", {
  wait: 2500, before: async (p) => { await p.click(".chip:nth-child(3)"); await p.waitForTimeout(1500); },
});
await shot("watch-many", "#/watch/s11", { wait: 2500 });
await shot("watch-gone", "#/watch/s99", { wait: 2000 });
await shot("menu-open", "#/sources", {
  width: 390, fullPage: false, before: async (p) => { await p.click(".menu-btn"); await p.waitForTimeout(400); },
});

await scenario({ sources: 12, bridges: "running" });
await shot("bridges-running", "#/bridges", { wait: 2500 });
await shot("sources-bridged", "#/sources", { wait: 2500 });

await scenario({ sources: 12, bridges: "error" });
await shot("bridges-error", "#/bridges", { wait: 2500 });

await scenario({ sources: 12, bridges: "mixed" });
await shot("bridges-mixed", "#/bridges", { wait: 2500 });

await scenario({ sources: 12, bridges: "running", offline: false });
await shot("offline", "#/sources", {
  wait: 2500, setup: () => scenario({ offline: false }),
  before: async (p) => { await scenario({ offline: true }); await p.waitForTimeout(2500); },
});
await shot("offline-bridges", "#/bridges", {
  wait: 2500, setup: () => scenario({ offline: false }),
  before: async (p) => { await scenario({ offline: true }); await p.waitForTimeout(2500); },
});
await scenario({ offline: false });

await browser.close();
if (failures.length) {
  console.error(`\n${failures.length} overflow failure(s):\n${failures.join("\n")}`);
  process.exit(1);
}
