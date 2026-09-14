// Native Chrome evidence for listbox capture, stationary scrolling and commit timing.
const {launch} = require('./chrome_test_browser.cjs');
const assert=require('node:assert/strict');
(async()=>{
    const browser=await launch({headless:true,executablePath:process.argv[2]});
    let checks=0;
    const check=(got,want)=>{assert.deepEqual(got,want);++checks;};
    const wait=ms=>new Promise(resolve=>setTimeout(resolve,ms));
    try {
        const page=await browser.newPage(); await page.setViewport({width:640,height:480});
        async function setup(multiple=true) {
            await page.setContent(`<style>body{margin:0}select{position:absolute;left:100px;top:100px;width:220px;height:110px;padding:0;border:0}option{height:24px;padding:0}</style><select id=s size=4 ${multiple?'multiple':''}>${Array.from({length:20},(_,i)=>`<option id=o${i} value=${i} ${i===5||i===19?'disabled':''}>Row ${i}</option>`).join('')}</select>`);
            await page.evaluate(()=>{window.events=[];s.oninput=()=>events.push('input');s.onchange=()=>events.push('change')});
        }
        async function row(i) {
            const p=await page.$eval('#o'+i,o=>{const r=o.getBoundingClientRect();return[r.x+r.width/2,r.y+r.height/2]});
            await page.mouse.move(...p);
        }
        const selected=()=>page.evaluate(()=>[...s.selectedOptions].map(o=>+o.value));
        const events=()=>page.evaluate(()=>events.splice(0));
        const scroll=()=>page.evaluate(()=>s.scrollTop);
        for(const multiple of [false,true]) {
            await setup(multiple);
            await row(1);await page.mouse.down();await row(2);await page.mouse.move(210,240);
            const before=await selected(); check(before,multiple?[1,2]:[2]); check(await events(),[]);
            await page.waitForFunction(()=>s.scrollTop>0,{timeout:2500});
            await page.waitForFunction(()=>s.scrollTop===s.scrollHeight-s.clientHeight,{timeout:4000});
            check(await selected(),before);check(await events(),[]);
            await row(18);
            const expected=multiple?Array.from({length:18},(_,i)=>i+1).filter(i=>i!==5):[18];
            check(await selected(),expected);check(await events(),[]);
            await page.mouse.move(210,240);await page.mouse.up();check(await events(),['input','change']);
            const bottom=await scroll();await wait(250);check(await scroll(),bottom);check(await selected(),expected);
        }
        await setup();await row(1);await page.mouse.down();await page.mouse.move(210,240);
        await wait(350);check(await scroll(),0); // Needs a captured mousemove inside first.
        await row(2);await page.mouse.move(400,155);await wait(350);check(await scroll(),0);
        await page.mouse.move(400,240);
        await page.waitForFunction(()=>s.scrollTop>0,{timeout:2500});
        check(await events(),[]);
        await page.mouse.move(210,70);
        await page.waitForFunction(()=>s.scrollTop===0,{timeout:4000});check(await scroll(),0);
        await page.mouse.up();check(await events(),['input','change']);
        console.log(`Chrome select autoscroll: ${checks} checks, 0 failures (${await browser.version()})`);
    } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
