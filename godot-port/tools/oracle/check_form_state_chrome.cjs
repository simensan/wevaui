// Browser evidence for live/default state, reset, and selector semantics.
const puppeteer = require('puppeteer');
const assert = require('node:assert/strict');
(async () => {
    const browser = await puppeteer.launch({headless:true, executablePath:process.argv[2]});
    let checks = 0;
    const check = (actual, expected) => { assert.deepEqual(actual, expected); ++checks; };
    try {
        const page = await browser.newPage();
        for (const tag of ['input', 'textarea']) {
            await page.setContent(`<form id=f><${tag} id=t ${tag==='input'?'value="seed"':''}>${tag==='textarea'?'seed':''}</${tag}><button id=r type=reset>Reset</button></form>`);
            const state = () => page.$eval('#t', e => [e.value,e.defaultValue,e.matches(':placeholder-shown')]);
            check(await state(), ['seed','seed',false]);
            await page.$eval('#t', e => {e.value='edited';e.defaultValue='next';});
            check(await state(), ['edited','next',false]);
            await page.$eval('#f', f => f.reset()); check(await state(), ['next','next',false]);
            await page.$eval('#t', e => e.defaultValue='later'); check(await state(), ['later','later',false]);
            await page.$eval('#t', e => {e.value=e.value;e.defaultValue='same-value setter dirties';});
            check(await state(), ['later','same-value setter dirties',false]);
            await page.$eval('#t', e => {e.value='';e.placeholder='hint';});
            check(await state(), ['','same-value setter dirties',true]);
            await page.$eval('#t', e => {e.value='\r\na\rb\n';});
            check((await state())[0], tag==='input'?'ab':'\na\nb\n');
            await page.evaluate(() => {
                window.events=[];
                for (const type of ['reset','input','change']) f.addEventListener(type,e=>events.push(e.type));
                t.defaultValue='reset';t.disabled=true;t.readOnly=true;
            });
            await page.click('#r'); check(await state(), ['reset','reset',false]);
            check(await page.evaluate(()=>events), ['reset']);
            await page.$eval('#t', e => {e.disabled=false;e.readOnly=false;e.focus();e.value='edit';e.setSelectionRange(1,2);});
            await page.$eval('#f', f => f.reset());
            check(await page.$eval('#t', e => [document.activeElement===e,e.selectionStart,e.selectionEnd]), [true,5,5]);
            await page.$eval('#t', e => {e.value='clone';window.copy=e.cloneNode(true);copy.defaultValue='new clone default';});
            check(await page.evaluate(()=>[copy.value,copy.defaultValue]), ['clone','new clone default']);
        }
        await page.setContent(`<form id=f><input id=c type=checkbox checked><input id=r1 type=radio name=g checked><input id=r2 type=radio name=g checked><input id=n1 type=radio checked><input id=n2 type=radio checked><select id=s><option id=o1 selected>A</option><option id=o2 selected>B</option><option id=o3 value="">C</option></select><select id=list size=4><option>D</option></select><select id=multi multiple><option selected>E</option><option selected>F</option></select></form><form id=other><input id=excluded value=other></form><input id=external form=f value=outside>`);
        const state = () => page.evaluate(() => ({marks:[c.checked,r1.checked,r2.checked,n1.checked,n2.checked],defaults:[c.defaultChecked,r1.defaultChecked,r2.defaultChecked],select:[s.value,...Array.from(s.options,o=>o.selected)],optionDefaults:Array.from(s.options,o=>o.defaultSelected),list:list.selectedIndex,multi:Array.from(multi.options,o=>o.selected)}));
        const initial={marks:[true,false,true,true,true],defaults:[true,true,true],select:['B',false,true,false],optionDefaults:[true,true,false],list:-1,multi:[true,true]};
        check(await state(),initial);
        await page.evaluate(()=>{c.checked=false;r1.checked=true;s.value='';document.getElementById('external').value='edited';excluded.value='keep';});
        check((await state()).marks,[false,true,false,true,true]);
        check((await state()).select,['',false,false,true]);
        check(await page.evaluate(()=>[c.matches('[checked]'),c.matches(':checked'),o2.matches('[selected]'),o2.matches(':checked')]),[true,false,true,false]);
        await page.evaluate(()=>{c.defaultChecked=false;c.defaultChecked=true;f.reset();});
        check(await state(),initial);check(await page.evaluate(()=>[document.getElementById('external').value,excluded.value]),['outside','keep']);
        await page.evaluate(()=>s.value='missing');check((await state()).select,['',false,false,false]);
        await page.evaluate(()=>{o1.selected=false;});check((await state()).select,['',false,false,false]);
        await page.evaluate(()=>f.reset());check(await state(),initial);
        await page.evaluate(()=>{o2.remove();});check((await state()).select,['A',true,false]);
        await page.evaluate(()=>{s.innerHTML='<option disabled>disabled</option><optgroup disabled><option>group</option></optgroup><option>enabled</option>';});
        check((await state()).select,['enabled',false,false,true]);
        await page.evaluate(()=>{s.value='disabled';f.reset();});check((await state()).select,['enabled',false,false,true]);
        for (const type of ['text','search','tel','password','url','email','number','unknown','TEXT','range','hidden']) {
            await page.setContent(`<form id=f><input id=t type=${type} value=seed></form>`);
            await page.$eval('#t',e=>e.value=' \ta\r\nb\n ');
            check(await page.$eval('#t',e=>e.value), type==='number'?'':type==='range'?'50':type==='hidden'?' \ta\r\nb\n ':['url','email'].includes(type)?'ab':' \tab ');
        }
        await page.setContent('<form id=f><input id=t type=range value=2 step=3 max=20></form>');
        await page.$eval('#t',t=>t.value='7');check(await page.$eval('#t',t=>[t.value,t.defaultValue]),['8','2']);
        await page.$eval('#t',t=>t.value='10');check(await page.$eval('#t',t=>[t.value,t.defaultValue]),['11','2']);
        await page.$eval('#f',f=>f.reset());check(await page.$eval('#t',t=>t.value),'2');
        const cdp = await page.createCDPSession();
        for (const property of ['textContent','innerHTML','defaultValue']) {
            await page.setContent('<form id=f><textarea id=t>abcd</textarea></form>');
            await page.$eval('#t',t=>{t.focus();t.setSelectionRange(1,3);});
            await cdp.send('Input.imeSetComposition',{text:'preview',selectionStart:7,selectionEnd:7});
            await page.$eval('#t',(t,prop)=>t[prop]='x',property);
            check(await page.$eval('#t',t=>[t.value,t.textContent]),['apreviewd','x']);
            await cdp.send('Input.insertText',{text:'late'});
            check(await page.$eval('#t',t=>[t.value,t.textContent]),['alated','x']);
        }
        for (const tag of ['input','textarea']) {
            await page.setContent(`<form id=f><${tag} id=t></${tag}></form><button id=other>Other</button>`);
            await page.$eval('#t',t=>{t.defaultValue='same';t.focus();t.setSelectionRange(1,2);});
            await page.$eval('#f',f=>f.reset());
            check(await page.$eval('#t',t=>[t.selectionStart,t.selectionEnd]),[1,2]);
            for (const user of [false,true]) for (const replacement of ['same','samex','different']) {
                await page.$eval('#t',t=>{t.value='same';t.focus();t.setSelectionRange(4,4);window.events=[];t.onchange=()=>events.push(t.value);});
                if (user) await page.keyboard.type('x');
                await page.$eval('#t',(t,value)=>t.value=value,replacement); await page.focus('#other');
                check(await page.evaluate(()=>events),user && replacement!=='same'?[replacement]:[]);
            }
        }
        await page.setContent('<input id=t type=number>');
        for (const [value,expected] of [['1.e2','1.e2'],['1.',''],['-0','-0'],['+1',''],['01','01'],
                ['1e2','1e2'],['1e+2','1e+2'],['1e-2','1e-2'],['.5','.5'],['-.5','-.5'],['0x1',''],['1\t','']]) {
            await page.$eval('#t',(t,value)=>t.value=value,value); check(await page.$eval('#t',t=>t.value),expected);
        }
        console.log(`Chrome form state: ${checks} checks, 0 failures (${await browser.version()})`);
    } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
