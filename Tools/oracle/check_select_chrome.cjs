// Native Chrome evidence for the matching test_select_controls.cpp sequence.
const puppeteer = require('puppeteer');
const assert = require('node:assert/strict');
(async () => {
    const browser = await puppeteer.launch({headless:true, executablePath:process.argv[2]});
    let checks = 0;
    const check = (got, want) => { assert.deepEqual(got, want); ++checks; };
    try {
        const page = await browser.newPage();
        async function setup(multiple=true, selected='', size='4') {
            await page.setContent(`<form id=f><select id=s ${multiple?'multiple':''} size="${size}" style="width:240px;height:210px">${[...'abcdegh'].map(c => `${c==='e'?'<optgroup disabled label=Off>':''}<option id=o${c} value=${c} ${c==='c'?'disabled':''} ${selected.includes(c)?'selected':''}>${c}</option>${c==='e'?'</optgroup>':''}`).join('')}</select></form>`);
            await page.evaluate(() => { s.focus(); window.events=[]; s.oninput=()=>events.push('input'); s.onchange=()=>events.push('change'); });
        }
        async function state(selected, changed=true) {
            check(await page.evaluate(() => [[...s.options].filter(o=>o.selected).map(o=>o.value).join(''), events.splice(0)]),
                  [selected, changed?['input','change']:[]]);
        }
        async function key(code, mods=[]) {
            for (const m of mods) await page.keyboard.down(m);
            await page.keyboard.press(code);
            for (const m of [...mods].reverse()) await page.keyboard.up(m);
        }
        async function move(row) {
            const pos=await page.$eval('#o'+row, e=>{const r=e.getBoundingClientRect();return [r.x+r.width/2,r.y+r.height/2]});
            await page.mouse.move(...pos);
        }
        async function click(row, mods=[]) {
            for (const m of mods) await page.keyboard.down(m);
            await move(row); await page.mouse.down(); await page.mouse.up();
            for (const m of [...mods].reverse()) await page.keyboard.up(m);
        }
        for (const multiple of [false,true]) {
            await setup(multiple);
            await key('ArrowDown'); await state('a');
            await key('ArrowDown'); await state('b');
            await key('ArrowUp',['Shift']); await state(multiple?'ab':'a');
            await key('ArrowDown',['Shift']); await state('b');
            await key('ArrowDown',['Control']); await state(multiple?'b':'d',!multiple);
            await key(' ',['Control']); await state(multiple?'bd':'d',multiple);
            await key('ArrowDown'); await state('g');
            await key('Home'); await state('a');
            await key('End',['Shift']); await state(multiple?'abdgh':'h');
            await key('PageUp'); await state('d');
            await key('PageDown'); await state('g');
            await key('a',['Control']); await state(multiple?'abdgh':'g',multiple);
            await key('Enter'); await state(multiple?'abdgh':'g',false);
            await key(' '); await state(multiple?'abdgh':'g',false);
        }
        await setup();
        for (const [row,mods,selected,changed] of [
            ['b',[],'b',true],['g',['Control'],'bg',true],['d',['Shift'],'dg',true],
            ['a',['Shift'],'abdg',true],['h',['Control','Shift'],'gh',true],
            ['d',[],'d',true],['d',[],'d',false],['d',['Control'],'',true],
            ['b',['Shift'],'bd',true],['c',[],'bd',false],['e',[],'bd',false]]) {
            await click(row,mods); await state(selected,changed);
        }
        await setup(true,'bg');
        await move('b'); await page.mouse.down(); await state('b',false);
        await move('g'); await state('bdg',false);
        await page.mouse.up(); await state('bdg');
        await page.keyboard.down('Control');
        await move('b'); await page.mouse.down(); await state('dg',false);
        await move('g'); await state('',false);
        await page.mouse.up(); await state(''); await page.keyboard.up('Control');
        await setup(true,'bg'); await key('ArrowDown'); await state('d');
        await setup(); await key('ArrowUp'); await state('h');
        await setup(true,'c'); await click('b'); await state('b');
        for (const [sizes, listbox] of [
            [['','0','1','01','+1','1x','-1','x','-0','4294967296'],false],
            [['2','2x','2.5','+2','4294967295'],true]]) {
            for (const size of sizes) {
                await setup(false,'',size);
                check(await page.evaluate(() => [s.value, s.options[0].getBoundingClientRect().height>0]),[listbox?'':'a',listbox]);
            }
        }
        await setup(false,'a','1');
        for (let i=0;i<3;++i) for (const size of ['4','1']) {
            await page.$eval('#s',(s,size)=>s.setAttribute('size',size),size);
            check(await page.evaluate(()=>[s.value,s.options[0].getBoundingClientRect().height>0]),['a',size==='4']);
        }
        console.log(`Chrome select controls: ${checks} checks passed (${await browser.version()})`);
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode=1; });
