// Real browser rendering trace. GPU-process CPU slices are NOT GPU execution time.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const {launch} = require('../oracle/chrome_test_browser.cjs');

(async () => {
    const [fixturePath, output, framesArg = '180', roundsArg = '3'] = process.argv.slice(2);
    if (!fixturePath || !output) throw new Error('Usage: render_chrome.cjs FIXTURE_JSON NEW_OUTPUT_DIR [frames] [rounds]');
    const frames = Number(framesArg), rounds = Number(roundsArg);
    if (!Number.isInteger(frames) || frames < 1 || !Number.isInteger(rounds) || rounds < 1)
        throw new Error('Positive frame/round counts required');
    fs.mkdirSync(output, {recursive: false});
    const fixture = JSON.parse(fs.readFileSync(fixturePath, 'utf8'));
    const assets = path.resolve(__dirname, '../../hosts/godot/project/samples/western_survival/assets');
    const server = http.createServer((req, res) => {
        const url = new URL(req.url, 'http://localhost');
        if (url.pathname === '/') {
            res.setHeader('Content-Type', 'text/html; charset=utf-8');
            res.end(`<!doctype html><html><head><style>${fixture.css}
                @font-face{font-family:Exported;src:url('/font.ttf');font-weight:400}
                html,body,kbd{font-family:Exported,sans-serif}
                .world{position:absolute;inset:0;background:url('/assets/frontier.png') center/cover}
                .shade{position:absolute;inset:0;background:rgba(8.925,14.025,8.925,.16)}
                .sky{position:absolute;left:0;right:0;top:0;height:330px;background:linear-gradient(rgba(6.375,8.925,6.375,.6),transparent)}
                </style></head><body><div class="world"></div><div class="shade"></div><div class="sky"></div>${fixture.html}</body></html>`);
        } else if (url.pathname === '/font.ttf') {
            res.setHeader('Content-Type', 'font/ttf'); res.end(fs.readFileSync(fixturePath + '.ttf'));
        } else if (/^\/assets\/[a-zA-Z0-9_.-]+$/.test(url.pathname)) {
            const file = path.join(assets, path.basename(url.pathname));
            if (!fs.existsSync(file)) {res.writeHead(404); res.end(); return;}
            res.setHeader('Content-Type', file.endsWith('.svg') ? 'image/svg+xml' : 'image/png');
            res.end(fs.readFileSync(file));
        } else {res.writeHead(404); res.end();}
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    let browser;
    try {
        browser = await launch({headless: false, args: ['--enable-gpu-service-tracing',
            '--disable-backgrounding-occluded-windows', '--window-size=1320,820']});
        const system = await browser.target().createCDPSession();
        const gpuInfo = await system.send('SystemInfo.getInfo');
        const page = await browser.newPage();
        await page.setViewport({width:1280,height:720,deviceScaleFactor:1});
        const errors = [];
        page.on('pageerror', error => errors.push(String(error)));
        page.on('requestfailed', req => errors.push(req.url() + ': ' + req.failure()?.errorText));
        await page.goto(`http://127.0.0.1:${server.address().port}/`, {waitUntil:'networkidle0'});
        await page.evaluate(async fixture => {
            const escape = s => String(s).replaceAll('&','&amp;').replaceAll('"','&quot;').replaceAll('<','&lt;');
            document.querySelector('#inventory-grid').innerHTML = Array.from({length:24}, (_, i) => {
                const item = fixture.items[i];
                return item ? `<button id="item-${i}" class="inventory-slot" title="${escape(item.name)}"><img src="assets/${item.id}.svg" alt="${escape(item.name)}"><span id="count-${i}" class="stack">${item.count}</span></button>`
                    : '<button class="inventory-slot empty" disabled aria-label="Empty inventory slot">+</button>';
            }).join('');
            document.querySelector('#recipe-costs').innerHTML = Object.entries(fixture.recipes[0].cost).map(([id,need]) => {
                const item = fixture.items.find(i => i.id === id);
                return `<div class="material${item.count < need ? ' short' : ''}"><img src="assets/${id}.svg" alt=""><span>${escape(item.name)}</span><strong>${item.count} / ${need}</strong></div>`;
            }).join('');
            const node = id => document.getElementById(id);
            const apply = state => {
                for (const [id, data] of Object.entries(state)) {
                    const e = node(id); if (!e) throw new Error('Missing #' + id);
                    for (const [name,value] of Object.entries(data.attributes)) {
                        if (value === null) e.removeAttribute(name); else e.setAttribute(name,value);
                    }
                    if ('text' in data) e.textContent = data.text;
                }
            };
            window.prepare = name => {
                apply(fixture.states[name.startsWith('inventory') ? 'inventory' : 'hud']);
                node('survival').style.visibility = name === 'background' ? 'hidden' : '';
                if (name.startsWith('inventory')) node('tab-inventory').focus();
                else document.activeElement?.blur();
            };
            window.change = (name, i) => {
                if (name === 'updating_hud') {
                    const value = 30 + i % 70;
                    node('stamina-value').textContent = String(value);
                    node('stamina-fill').style.width = value + '%';
                } else if (name === 'inventory_selection') {
                    const index = i % 12, state = fixture.selection_states[index];
                    for (let j=0;j<12;j++) node('item-'+j).classList.toggle('chosen',j===index);
                    for (const id of ['item-name','item-category','item-description','item-weight','item-quantity','item-action'])
                        node(id).textContent = state[id].text;
                    node('detail-icon').setAttribute('src',state['detail-icon'].attributes.src);
                    node('item-action').toggleAttribute('disabled',state['item-action'].attributes.disabled !== null);
                }
            };
            window.geometry = () => Object.fromEntries([...document.querySelectorAll('[id]')].map(e => {
                const r=e.getBoundingClientRect();return [e.id,[r.x,r.y,r.width,r.height]];
            }));
            window.runFrames = (name, count, measured) => new Promise(resolve => {
                let i=0,next=0,previous=0; const intervals=[], setters=[];
                function tick(now) {
                    if (now + .1 < next) {requestAnimationFrame(tick);return;}
                    next = Math.max(next + 1000/60, now);
                    if (previous) intervals.push(now-previous);previous=now;
                    const start=performance.now();window.change(name,i + (measured ? 120 : 0));
                    setters.push(performance.now()-start);
                    if (++i < count) requestAnimationFrame(tick);
                    else requestAnimationFrame(() => resolve({intervals,setters,updates:count,measured}));
                }
                requestAnimationFrame(tick);
            });
            window.prepare('idle_hud');
            await document.fonts.ready;
            await Promise.all([...document.images].map(image => image.decode()));
        }, fixture);
        const results=[];
        for (let round=0;round<rounds;round++) {
            const cases=['background','idle_hud','updating_hud','inventory','inventory_selection'];
            if (round%2) cases.reverse();
            for (const name of cases) {
                await page.evaluate(name => window.prepare(name),name);
                await page.evaluate(name => window.runFrames(name,120,false),name);
                const before = await page.evaluate(() => window.geometry());
                const tracePath=path.join(output,`${round}-${name}-trace.json`);
                await page.tracing.start({path:tracePath,categories:['devtools.timeline','blink.user_timing','cc','gpu','viz',
                    'disabled-by-default-gpu.service','disabled-by-default-gpu.device','disabled-by-default-devtools.timeline.frame']});
                const result = await page.evaluate(async ({name,frames}) => {
                    performance.mark('renderbench:start');
                    const result = await window.runFrames(name,frames,true);
                    performance.mark('renderbench:end');return result;
                }, {name,frames});
                await page.tracing.stop();
                const after=await page.evaluate(() => window.geometry());
                if (round===0) await page.screenshot({path:path.join(output,name+'.png')});
                results.push({name,round,...result,before,after,trace:path.basename(tracePath)});
                console.log(name,round,'complete');
            }
        }
        if (errors.length) throw new Error(errors.join('\n'));
        fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({schema:1,browser:await browser.version(),
            gpuInfo,viewport:[1280,720],target_updates_per_second:60,frames,rounds,results},null,2));
    } finally {
        try {if (browser) await browser.close();}
        finally {await new Promise(resolve=>server.close(resolve));}
    }
})().catch(error=>{console.error(error);process.exitCode=1;});
