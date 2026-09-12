// Matching behavioral evidence for test_typeahead.cpp. Run with a Chrome path.
const puppeteer = require('puppeteer');
const assert = require('node:assert/strict');
(async () => {
    const browser = await puppeteer.launch({headless:true, executablePath:process.argv[2]});
    let checks = 0;
    const check = (got, want) => { assert.deepEqual(got, want); ++checks; };
    try {
        const page = await browser.newPage();
        const input = await page.createCDPSession();
        async function type(text, modifiers=0) {
            for (const character of text)
                await input.send('Input.dispatchKeyEvent', {type:'char', text:character, modifiers});
        }
        async function focus() {
            await page.evaluate(() => { other.focus(); s.focus(); window.events=[]; });
        }
        async function state(value, mode, changes=1) {
            check(await page.evaluate(() => [[...s.selectedOptions].map(o=>o.value).join(','), events.splice(0)]),
                  [value, Array.from({length:changes},()=>mode===0?['input','change']:['change']).flat()]);
        }
        for (const mode of [0,1,2]) {
            await page.setContent(`<select id=s ${mode===1?'size=6':mode===2?'multiple size=6':''}>
                <option value=a>Alpha</option><option value=al>Alpine</option>
                <option id=beta value=b label=Beta>Submitted beta text</option>
                <option value=br>Bravo</option><option value=bl>Blue bird</option>
                <option value=bu>Bumblebee</option><option value=c>Cherry</option>
                <option disabled>Bear</option><optgroup label=Disabled disabled><option>Banana</option></optgroup>
                </select><button id=other>Other</button>`);
            await page.evaluate(() => { s.oninput=()=>events.push('input'); s.onchange=()=>events.push('change'); });
            await focus();
            await type('b'); await state('b',mode);
            await type('b'); await state('br',mode);
            await type('l'); await state('br',mode,0);
            await type('x'); await state('br',mode,0);
            await focus();
            await type('a'); await state('a',mode);
            await type('l'); await state('a',mode,mode===0?0:1);
            await type('p'); await state('a',mode,mode===0?0:1);
            await type('i'); await state('al',mode);
            await focus();
            await type('blue'); await state('bl',mode,mode===0?2:4);
            await page.keyboard.press(' '); await state('bl',mode,mode===0?0:1);
            await type('bi'); await state('bl',mode,mode===0?0:2);
            await focus();
            for (const modifier of [1,2,4]) { // CDP: Alt, Ctrl, Meta
                await type('b',modifier); await state('bl',mode,0);
            }
            await type('B',8); await state('bu',mode);
            await type('B',8); await state('b',mode);
            await focus();
            await page.evaluate(() => beta.label='Delta');
            await type('d'); await state('b',mode,mode===0?0:1);
        }
        const pairs = [
            ['e','Éclair',true],['é','Eclair',true],['é','e\u0301clair',true],
            ['a','Åland',true],['o','Örebro',true],['ae','Æther',true],['a','Æther',false],
            ['s','ßeta',false],['ss','ßeta',true],['strasse','Straße',true],['oe','Œuvre',true],
            ['i','İzmir',true],['i','ıstanbul',false],['lodz','Łódź',true],['o','Øresund',true],
            ['a','Ａlpha',true],['ffi','ﬃeld',true],['f','ﬃeld',false],
            ['и','Йога',false],['е','Ёлка',true],['σ','Σίγμα',true],['か','カナ',true],
            ['ば','はな',true],['한','한글',true]
        ];
        for (const [prefix,label,matches] of pairs) {
            await page.setContent('<select id=s><option>initial</option><option id=choice value=hit></option></select><button id=other>Other</button>');
            // A distinct first character avoids repeated-key cycling. Resetting
            // selection before each character proves the complete final prefix,
            // rather than accepting a choice made by an earlier short prefix.
            await page.evaluate(label => { choice.label='z'+label; s.focus(); },label);
            for (const character of 'z'+prefix) {
                await page.evaluate(() => { s.selectedIndex=0; });
                await type(character);
            }
            check(await page.evaluate(()=>s.value),matches?'hit':'initial');
        }
        await page.setContent('<select id=s><option>initial</option><option value=hit label="  Bravo">different</option></select><button id=other>Other</button>');
        await page.focus('#s'); await type('b');
        check(await page.$eval('#s',s=>s.value),'hit');
        await focus(); await type('x');
        await new Promise(resolve=>setTimeout(resolve,1100));
        await type('b'); check(await page.$eval('#s',s=>s.value),'hit');
        await page.setContent('<select id=s><option>initial</option><option value=hit label="">Bravo</option></select>');
        await page.focus('#s'); await type('b');
        check(await page.$eval('#s',s=>s.value),'hit');
        console.log(`Chrome typeahead: ${checks} checks passed (${await browser.version()})`);
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode=1; });
