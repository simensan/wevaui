// Trace the exact HTML/CSS/PNG/font and setter states consumed by weva_cost.
// Trace CPU slices are not hardware GPU execution or presented-frame latency.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const crypto = require('node:crypto');
const {launch} = require('../oracle/chrome_test_browser.cjs');

(async () => {
    const [directory, output, roundsArg = '3'] = process.argv.slice(2);
    if (!directory || !output) throw new Error('Usage: render_prepared_chrome.cjs FIXTURES NEW_OUTPUT [rounds]');
    const rounds = Number(roundsArg);
    if (!Number.isInteger(rounds) || rounds < 1) throw new Error('Positive rounds required');
    const root = path.resolve(directory);
    const files = new Map(fs.readdirSync(root).filter(n => /\.(html|css|ttf|png|json)$/.test(n))
        .map(n => [n, fs.readFileSync(path.join(root, n))]));
    const rewrite = text => text.replaceAll(root.replaceAll('\\', '/') + '/', '/assets/');
    const cases = ['hud', 'inventory'];
    // Agentic refresh_bindings resolves the fixture's unbound Depth to empty.
    const markup = Object.fromEntries(cases.map(name => {
        const html = rewrite(files.get(name+'.html').toString()).replaceAll('{{ Depth }}', '');
        if (html.includes('{{')) throw new Error('Unsupported fixture binding in '+name);
        return [name, html];
    }));
    const states = JSON.parse(files.get('mutations.json'));
    if (states.length !== 2 || states.some(s => !s.length)) throw new Error('Two nonempty mutation cases required');
    fs.mkdirSync(output, {recursive: false});
    const errors = [];
    const server = http.createServer((req, res) => {
        const url = new URL(req.url, 'http://localhost');
        const name = url.pathname.slice(1);
        if (cases.includes(name)) {
            res.setHeader('Content-Type', 'text/html; charset=utf-8');
            res.end(`<!doctype html><html><head><style>${rewrite(files.get(name+'.css').toString())}</style></head><body>${markup[name]}</body></html>`);
        } else if (url.pathname.startsWith('/assets/') && files.has(url.pathname.slice(8))) {
            const asset = url.pathname.slice(8);
            res.setHeader('Content-Type', asset.endsWith('.png') ? 'image/png' : 'font/ttf');
            res.end(files.get(asset));
        } else {res.writeHead(404); res.end();}
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    let browser;
    try {
        browser = await launch({headless: false, args: ['--disable-backgrounding-occluded-windows',
            '--window-position=-10000,-10000', '--window-size=1320,820']});
        const system = await browser.target().createCDPSession();
        const gpuInfo = await system.send('SystemInfo.getInfo');
        const results = [];
        for (let round = 0; round < rounds; round++) {
            for (const name of round % 2 ? [...cases].reverse() : cases) {
                const page = await browser.newPage();
                page.on('pageerror', e => errors.push(String(e)));
                page.on('requestfailed', r => errors.push(r.url()+': '+r.failure()?.errorText));
                page.on('response', r => {if (r.status() >= 400 && !r.url().endsWith('/favicon.ico')) errors.push(r.url()+': '+r.status());});
                await page.setViewport({width:1280,height:720,deviceScaleFactor:1});
                const session = await page.createCDPSession();
                await session.send('Emulation.setDefaultBackgroundColorOverride', {color:{r:0,g:0,b:0,a:1}});
                await page.goto(`http://127.0.0.1:${server.address().port}/${name}`, {waitUntil:'networkidle0'});
                await page.evaluate(async states => {
                    window.states = states;
                    window.selectors = [...new Set(states.flat().map(m => m.selector))];
                    window.geometry = () => Object.fromEntries(window.selectors.map(selector => {
                        const e = document.querySelector(selector);
                        if (!e) throw new Error('Missing target '+selector);
                        const r = e.getBoundingClientRect(); return [selector,[r.x,r.y,r.width,r.height]];
                    }));
                    window.frames = (count, offset, mutate) => new Promise(resolve => {
                        let i=0, previous=0, next=0; const setters=[], intervals=[];
                        const tick = now => {
                            if (now+.1 < next) {requestAnimationFrame(tick); return;}
                            next = Math.max(next+1000/60, now);
                            if (previous) intervals.push(now-previous); previous=now;
                            const start=performance.now();
                            if (mutate) for (const m of window.states[(i+offset)%window.states.length]) {
                                const e=document.querySelector(m.selector);
                                if (!e) throw new Error('Missing mutation target '+m.selector);
                                if ('attribute' in m) {
                                    if (m.value===null) e.removeAttribute(m.attribute);
                                    else e.setAttribute(m.attribute,m.value);
                                } else e.textContent=m.value;
                            }
                            setters.push(performance.now()-start);
                            if (++i<count) requestAnimationFrame(tick);
                            else requestAnimationFrame(() => resolve({updates:count,setters,intervals}));
                        }; requestAnimationFrame(tick);
                    });
                    await document.fonts.ready;
                    await Promise.all([...document.images].map(i => i.decode()));
                    if (!document.fonts.check('14px Exported')) throw new Error('Prepared font not loaded');
                }, JSON.parse(rewrite(JSON.stringify(states[cases.indexOf(name)]))));
                const initial = await page.evaluate(() => window.geometry());
                await page.evaluate(() => window.frames(20,0,true));
                for (const phase of ['mutating','retained']) {
                    const trace = `${round}-${name}-${phase}-trace.json`;
                    await page.tracing.start({path:path.join(output,trace),categories:
                        ['devtools.timeline','blink.user_timing','cc','gpu','viz','disabled-by-default-devtools.timeline.frame']});
                    const stats = await page.evaluate(async phase => {
                        performance.mark('renderbench:start');
                        const result=await window.frames(200,20,phase==='mutating');
                        performance.mark('renderbench:end');
                        performance.clearMarks('renderbench:start');
                        performance.clearMarks('renderbench:end');
                        return result;
                    }, phase);
                    await page.tracing.stop();
                    const final = await page.evaluate(() => window.geometry());
                    results.push({name,phase,round,...stats,initial,final,trace});
                    console.log(round,name,phase,'complete');
                }
                if (round===0) await page.screenshot({path:path.join(output,name+'.png')});
                await page.close();
            }
        }
        if (errors.length) throw new Error(errors.join('\n'));
        const hashes = Object.fromEntries([...files].map(([n,b]) => [n,crypto.createHash('sha256').update(b).digest('hex')]));
        fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({schema:1,
            browser:await browser.version(),gpuInfo,viewport:[1280,720],target_updates_per_second:60,
            frames:200,warmup:20,rounds,background:'black',unbound_depth:'empty',hashes,results},null,2));
    } finally {
        try {if (browser) await browser.close();}
        finally {await new Promise(resolve => server.close(resolve));}
    }
})().catch(e => {console.error(e); process.exitCode=1;});
