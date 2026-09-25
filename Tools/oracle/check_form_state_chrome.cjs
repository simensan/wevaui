// Browser evidence for live/default state, reset, and selector semantics.
const {launch} = require('./chrome_test_browser.cjs');
const assert = require('node:assert/strict');
(async () => {
    const browser = await launch({headless:true, executablePath:process.argv[2]});
    let checks = 0;
    const check = (actual, expected) => { assert.deepEqual(actual, expected); ++checks; };
    try {
        const page = await browser.newPage();
        {
            const dialog = await browser.newPage();
            for (const setup of ['', 'show','showModal','showPopover']) for (const method of ['show','showModal','showPopover','hidePopover','togglePopover']) {
                await dialog.setContent('<dialog id=d popover>Confirm</dialog>');
                if (setup) await dialog.$eval('#d',(e,m)=>e[m](),setup);
                let open=setup==='show'||setup==='showModal', modal=setup==='showModal', popup=setup==='showPopover', error='';
                if (method==='show') { if(open&&modal)error='InvalidStateError';else if(!open){open=true;popup=false;} }
                else if (method==='showModal') { if((open&&!modal)||popup)error='InvalidStateError';else{open=true;modal=true;} }
                else if (method==='hidePopover') popup=false;
                else { if(modal&&!popup)error='InvalidStateError';else popup=method==='togglePopover'?!popup:true; }
                check(await dialog.$eval('#d',(e,m)=>{let error='';try{e[m]();}catch(v){error=v.name;}return[error,e.open,e.matches(':modal'),e.matches(':popover-open')];},method),[error,open,modal,popup]);
            }
            for (const mode of ['auto','manual','hint']) for (const nested of [false,true]) for (const method of ['show','showModal']) {
                await dialog.setContent(nested?'<div id=p popover='+mode+'><dialog id=d>Confirm</dialog></div>':'<div id=p popover='+mode+'>Menu</div><dialog id=d>Confirm</dialog>');
                await dialog.$eval('#p',e=>e.showPopover());
                await dialog.$eval('#d',(e,m)=>e[m](),method);
                check(await dialog.$eval('#p',e=>e.matches(':popover-open')),nested||mode==='manual');
            }
            await dialog.setContent('<dialog id=d>Confirm</dialog>');
            await dialog.$eval('#d',e=>{window.states=[];e.addEventListener('toggle',e=>states.push(e.newState));e.showModal();});
            await dialog.waitForFunction(()=>states.length===1); check(await dialog.evaluate(()=>states.splice(0)),['open']);
            await dialog.$eval('#d',e=>e.showModal());
            await dialog.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
            check(await dialog.evaluate(()=>states),[]);
            await dialog.$eval('#d',e=>e.close());
            await dialog.waitForFunction(()=>states.length===1); check(await dialog.evaluate(()=>states),['closed']);
            await dialog.close();
        }
        {
            const popup = await browser.newPage();
            for (const mode of [null,'manual','hint','invalid','AUTO']) {
                await popup.setContent('<div id=p popover>Menu</div>');
                await popup.$eval('#p',e=>{window.toggles=[];e.addEventListener('toggle',e=>toggles.push(e.newState));e.showPopover();});
                await popup.waitForFunction(()=>toggles.length===1);
                check(await popup.evaluate(()=>toggles.splice(0)),['open']);
                await popup.$eval('#p',(e,mode)=>mode===null?e.removeAttribute('popover'):e.setAttribute('popover',mode),mode);
                if (mode!=='AUTO') await popup.waitForFunction(()=>toggles.length===1);
                else await popup.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
                check(await popup.evaluate(()=>toggles.splice(0)),mode==='AUTO'?[]:['closed']);
                if (mode===null) await popup.$eval('#p',e=>e.setAttribute('popover',''));
                if (mode!=='AUTO') {
                    await popup.$eval('#p',e=>e.showPopover());
                    await popup.waitForFunction(()=>toggles.length===1);
                    check(await popup.evaluate(()=>toggles.splice(0)),['open']);
                }
                await popup.$eval('#p',e=>{e.hidePopover();e.hidePopover();});
                await popup.waitForFunction(()=>toggles.length===1);
                check(await popup.evaluate(()=>toggles.splice(0)),['closed']);
            }
            await popup.close();
        }
        {
            const top = await browser.newPage();
            await top.setContent('<style>dialog:modal span{width:37px}dialog:not(:modal) span{width:73px}:popover-open span{width:41px}[popover]:not(:popover-open) span{width:79px}</style><dialog id=d data-modal><span id=label>Confirm</span></dialog><div id=p popover data-popover-open><span id=item>Menu</span></div>');
            check(await top.$eval('#d',e=>e.matches(':modal')),false);
            check(await top.$eval('#p',e=>e.matches(':popover-open')),false);
            await top.$eval('#d',e=>e.show()); check(await top.$eval('#label',e=>getComputedStyle(e).width),'73px');
            await top.$eval('#d',e=>{e.close();e.showModal();}); check(await top.$eval('#label',e=>getComputedStyle(e).width),'37px');
            await top.$eval('#d',e=>e.removeAttribute('data-modal')); check(await top.$eval('#d',e=>e.matches(':modal')),true);
            await top.$eval('#d',e=>e.removeAttribute('open')); check(await top.$eval('#d',e=>e.matches(':modal')),true);
            await top.$eval('#d',e=>e.setAttribute('open','')); check(await top.$eval('#label',e=>getComputedStyle(e).width),'37px');
            await top.$eval('#d',e=>e.close()); check(await top.$eval('#d',e=>e.matches(':modal')),false);
            await top.$eval('#p',e=>e.showPopover()); check(await top.$eval('#item',e=>getComputedStyle(e).width),'41px');
            await top.$eval('#p',e=>e.setAttribute('popover','AUTO')); check(await top.$eval('#p',e=>e.matches(':popover-open')),true);
            await top.$eval('#p',e=>e.removeAttribute('data-popover-open')); check(await top.$eval('#p',e=>e.matches(':popover-open')),true);
            await top.keyboard.press('Escape'); check(await top.$eval('#item',e=>getComputedStyle(e).width),'79px');
            await top.$eval('#p',e=>{e.showPopover();e.removeAttribute('popover');}); check(await top.$eval('#p',e=>e.matches(':popover-open')),false);
            await top.$eval('#p',e=>e.setAttribute('popover','')); check(await top.$eval('#item',e=>getComputedStyle(e).width),'79px');
            for (const mode of ['MANUAL','invalid']) {
                await top.$eval('#p',(e,mode)=>{e.setAttribute('popover',mode);e.showPopover();},mode);
                await top.keyboard.press('Escape'); check(await top.$eval('#p',e=>e.matches(':popover-open')),true);
                await top.$eval('#p',e=>e.hidePopover()); check(await top.$eval('#p',e=>e.matches(':popover-open')),false);
            }
            await top.$eval('#p',e=>{e.setAttribute('popover','auto');e.showPopover();});
            await top.mouse.click(10,10); check(await top.$eval('#p',e=>e.matches(':popover-open')),false);
            await top.close();
        }
        {
        const page = await browser.newPage();
        for (const attributes of ["command='--open'",'commandfor=missing',"command='' commandfor=''","type=invalid command='--open'"]) {
            await page.setContent('<form id=f><input id=field><button id=command '+attributes+'>Inspect</button><button id=apply>Apply</button></form>');
            await page.evaluate(()=>{window.events=[];document.onclick=e=>events.push('click:'+e.target.id);document.querySelector('form').addEventListener('submit',e=>{e.preventDefault();events.push('submit');});});
            await page.click('#command'); check(await page.evaluate(()=>events.splice(0)),['click:command']);
            await page.focus('#command'); await page.keyboard.press('Enter'); check(await page.evaluate(()=>events.splice(0)),['click:command']);
            await page.focus('#field'); await page.keyboard.press('Enter'); check(await page.evaluate(()=>events.splice(0)),['click:apply','submit']);
            await page.$eval('#apply',e=>e.disabled=true); await page.keyboard.press('Enter'); check(await page.evaluate(()=>events.splice(0)),[]);
            await page.$eval('#command',e=>e.type='SUBMIT'); await page.focus('#command'); await page.keyboard.press('Enter'); check(await page.evaluate(()=>events.splice(0)),['click:command','submit']);
        }
        await page.close();
        }
        await page.setContent('<style>button:default span{width:37px}button:not(:default) span{width:73px}</style><input id=external type=submit form=f disabled><form id=f><button id=auto><span id=label>Apply</span></button><input id=image type=image><button id=reset type=reset>Reset</button><input id=check type=checkbox checked><select id=select><option id=a selected>A</option><option id=b>B</option></select></form><button id=orphan>Outside</button>');
        check(await page.$eval('#external',e=>e.matches(':default')),true);
        for (const id of ['auto','image','reset','orphan']) check(await page.$eval('#'+id,e=>e.matches(':default')),false);
        for (const id of ['check','a']) check(await page.$eval('#'+id,e=>e.matches(':default')),true);
        await page.$eval('#check',e=>e.checked=false); await page.$eval('#select',e=>e.value='B');
        for (const id of ['check','a']) check(await page.$eval('#'+id,e=>e.matches(':default')),true);
        check(await page.$eval('#b',e=>e.matches(':default')),false);
        const defaultWidth=()=>page.$eval('#label',e=>getComputedStyle(e).width);
        check(await defaultWidth(),'73px');
        for (const [id,name,value,expected] of [
            ['external','form','missing','37px'],['external','form','f','73px'],
            ['external','type','button','37px'],['auto','command','toggle-popover','73px'],
            ['auto','type','SUBMIT','37px'],['f','id','renamed','37px'],
            ['external','type','image','37px'],['external','form','renamed','73px']]) {
            await page.$eval('#'+id,(e,args)=>e.setAttribute(...args),[name,value]);
            if (name==='command') {
                check(await page.$eval('#auto',e=>e.matches(':default')),false);
                // Chrome 152 updates matches() here but leaves descendant CSS
                // stale. A fresh parse verifies the selector's intended style.
                await page.setContent(await page.content());
            }
            check(await defaultWidth(),expected);
        }
        await page.$eval('#check',e=>e.removeAttribute('checked')); check(await page.$eval('#check',e=>e.matches(':default')),false);
        await page.$eval('#a',e=>e.removeAttribute('selected')); check(await page.$eval('#a',e=>e.matches(':default')),false);
        for (const type of ['text','search','tel','url','email','password','date','month','week','time','datetime-local','number','checkbox','radio','file','hidden','range','color','button','submit','reset','image','unknown']) {
            await page.setContent('<input id=f type="'+type+'">');
            const editable=!['checkbox','radio','file','hidden','range','color','button','submit','reset','image'].includes(type);
            for (const attribute of ['', 'readonly', 'disabled']) {
                await page.$eval('#f',(e,attribute)=>{e.removeAttribute('readonly');e.removeAttribute('disabled');if(attribute)e.setAttribute(attribute,'false');},attribute);
                const expected=editable&&!attribute;
                check(await page.$eval('#f',e=>[e.matches(':read-write'),e.matches(':read-only')]),[expected,!expected]);
            }
        }
        await page.setContent('<style>#child:read-write{width:37px}#child:read-only{width:73px}</style><div id=root contenteditable><span id=child>Text</span><span id=off contenteditable=false><i id=on contenteditable=TRUE>On</i></span><span id=invalid contenteditable=invalid>Inherited</span><span id=space contenteditable=" true ">Inherited</span><span id=plain contenteditable=plaintext-only>Plain</span><input id=input readonly contenteditable><input id=check type=checkbox contenteditable><button id=button disabled>Button</button><textarea id=area disabled contenteditable></textarea><select id=select disabled><option id=option>A</option></select></div><div id=ordinary>Text</div>');
        for (const id of ['root','child','on','invalid','space','plain','button','select','option'])
            check(await page.$eval('#'+id,e=>e.matches(':read-write')),true);
        for (const id of ['off','input','check','area','ordinary'])
            check(await page.$eval('#'+id,e=>e.matches(':read-only')),true);
        for (const value of ['true','false','invalid','PLAINTEXT-ONLY']) {
            await page.$eval('#root',(e,value)=>e.setAttribute('contenteditable',value),value);
            check(await page.$eval('#child',e=>getComputedStyle(e).width),['true','PLAINTEXT-ONLY'].includes(value)?'37px':'73px');
        }
        await page.setContent('<style>#field:read-write{width:37px}#field:read-only{width:73px}</style><fieldset id=group disabled><legend><input id=master></legend><input id=field><textarea id=area></textarea></fieldset>');
        check(await page.$eval('#master',e=>e.matches(':read-write')),true);
        for (const id of ['field','area']) check(await page.$eval('#'+id,e=>e.matches(':read-only')),true);
        await page.$eval('#group',e=>e.disabled=false);
        for (const id of ['field','area']) check(await page.$eval('#'+id,e=>e.matches(':read-write')),true);
        for (const type of ['text','checkbox','TEXT','range','unknown']) {
            await page.$eval('#field',(e,type)=>e.setAttribute('type',type),type);
            check(await page.$eval('#field',e=>getComputedStyle(e).width),['checkbox','range'].includes(type)?'73px':'37px');
        }
        await page.$eval('#area',e=>e.setAttribute('readonly','false'));
        check(await page.$eval('#area',e=>e.matches(':read-only')),true);
        await page.$eval('#area',e=>e.removeAttribute('readonly'));
        check(await page.$eval('#area',e=>e.matches(':read-write')),true);
        for (const type of ['text','search','tel','url','email','password','date','month','week','time','datetime-local','number','checkbox','radio','file','hidden','range','color','button','submit','reset','image','unknown']) {
            await page.setContent('<input id=f type="'+type+'" required disabled readonly>');
            const expected=!['hidden','range','color','button','submit','reset','image'].includes(type);
            check(await page.$eval('#f',e=>[e.matches(':required'),e.matches(':optional')]),[expected,!expected]);
            await page.$eval('#f',e=>e.removeAttribute('required'));
            check(await page.$eval('#f',e=>[e.matches(':required'),e.matches(':optional')]),[false,true]);
        }
        await page.setContent('<style>input:required{width:37px}input:optional{width:73px}</style><textarea id=a required></textarea><select id=s required><option>A</option></select><button id=b required>Go</button><div id=d required>Text</div><input id=i>');
        for (const [id,selector,expected] of [['a',':required',true],['s',':required',true],['b',':optional',true],['d',':required',false]])
            check(await page.$eval('#'+id,(e,selector)=>e.matches(selector),selector),expected);
        for (const value of ['',null,'false']) {
            await page.$eval('#i',(e,value)=>value===null?e.removeAttribute('required'):e.setAttribute('required',value),value);
            check(await page.$eval('#i',e=>getComputedStyle(e).width),value===null?'73px':'37px');
        }
        await page.setContent('<style>button:hover{background:rgb(204,0,0)}button:active{background:rgb(0,204,0)}</style><button id=off disabled style="width:100px;height:60px">Unavailable</button>');
        await page.$eval('#off', e=>{window.disabledEvents=[];for(const name of ['pointerenter','pointerdown','pointerup','click'])e.addEventListener(name,()=>window.disabledEvents.push(name));});
        await page.mouse.move(50,30);
        check(await page.$eval('#off',e=>getComputedStyle(e).backgroundColor),'rgb(204, 0, 0)');
        await page.mouse.down();
        check(await page.$eval('#off',e=>getComputedStyle(e).backgroundColor),'rgb(0, 204, 0)');
        await page.mouse.up();
        check(await page.$eval('#off',e=>document.activeElement===e),false);
        check(await page.evaluate(()=>window.disabledEvents),['pointerenter','pointerdown','pointerup']);
        await page.setContent('<style>input:disabled{width:37px}input:enabled{width:73px}</style><fieldset id=group disabled><div id=ordinary>Text</div><legend><input id=master><input id=own disabled></legend><input id=blocked><legend><input id=second></legend><fieldset><legend><input id=nested></legend></fieldset><select><optgroup disabled><option id=option>A</option></optgroup></select></fieldset>');
        check(await page.$eval('#master', e=>e.matches(':enabled')), true);
        for (const id of ['own','blocked','second','nested','option'])
            check(await page.$eval('#'+id, e=>e.matches(':disabled')), true);
        check(await page.$eval('#ordinary', e=>[e.matches(':enabled'),e.matches(':disabled')]), [false,false]);
        check(await page.$eval('#blocked', e=>getComputedStyle(e).width), '37px');
        await page.focus('#master'); await page.keyboard.type('on');
        check(await page.$eval('#master', e=>e.value), 'on');
        await page.$eval('#master', e=>e.blur()); await page.$eval('#blocked', e=>e.focus());
        check(await page.$eval('#blocked', e=>document.activeElement===e), false);
        await page.$eval('#group', e=>e.disabled=false);
        for (const id of ['blocked','second','nested']) check(await page.$eval('#'+id,e=>e.matches(':enabled')), true);
        check(await page.$eval('#own',e=>e.matches(':disabled')), true);
        check(await page.$eval('#blocked', e=>getComputedStyle(e).width), '73px');
        await page.focus('#blocked'); await page.$eval('#group',e=>e.disabled=true);
        check(await page.$eval('#blocked', e=>document.activeElement===e), false);
        await page.focus('#master'); await page.$eval('#group',e=>e.disabled=true);
        check(await page.$eval('#master', e=>document.activeElement===e), true);
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
        await page.setContent('<style>#behind{position:fixed;left:10px;top:10px;width:100px;height:40px}dialog{position:fixed;left:200px;top:100px;width:200px;height:100px;margin:0;padding:0}</style><button id="behind">Inventory</button><dialog id="modal"><button id="inside">Confirm</button></dialog><dialog id="second"><button id="other" autofocus>Apply</button></dialog>');
        check(await page.evaluate(()=>{behind.focus();modal.showModal();return document.activeElement.id;}),'inside');
        check(await page.evaluate(()=>{behind.focus();return document.activeElement.id;}),'inside');
        await page.evaluate(()=>{window.modalClicks=[];document.addEventListener('click',e=>modalClicks.push(e.target.id));});
        await page.mouse.click(30,30);
        check(await page.evaluate(()=>modalClicks),['modal']);
        await page.focus('#inside'); await page.keyboard.press('Tab');
        check(await page.evaluate(()=>document.activeElement.id==='behind'),false);
        await page.focus('#inside');
        check(await page.evaluate(()=>{second.showModal();return document.activeElement.id;}),'other');
        check(await page.evaluate(()=>{inside.focus();return document.activeElement.id;}),'other');
        check(await page.evaluate(()=>{second.close();return document.activeElement.id;}),'inside');
        check(await page.evaluate(()=>{modal.close();return document.activeElement.id;}),'behind');
        await page.evaluate(()=>{second.showModal();modal.showModal();window.modalClicks=[];});
        await page.click('#inside');
        check(await page.evaluate(()=>modalClicks),['inside']);
        await page.setContent('<style>#clip{transform:translate(90px,80px);opacity:0;overflow:hidden;width:10px;height:10px}#cover{position:fixed;inset:0;z-index:2147483647;background:red}dialog{position:fixed;left:200px;top:100px;width:150px;height:100px;margin:0;padding:0}</style><div id="clip"><dialog id="modal"><button id="inside">Confirm</button></dialog></div><div id="cover">Overlay</div>');
        check(await page.evaluate(()=>{modal.showModal();const r=modal.getBoundingClientRect();return [r.x,r.y];}),[200,100]);
        check(await page.evaluate(()=>{const r=inside.getBoundingClientRect();return document.elementFromPoint(r.x+r.width/2,r.y+r.height/2).id;}),'inside');
        await page.setContent('<div id="popup" popover></div>');
        check(await page.evaluate(()=>{popup.showPopover();const s=getComputedStyle(popup);return [s.top,s.right,s.bottom,s.left];}),['0px','0px','0px','0px']);
        for (const mode of ['normal','underlying','disabled','hidden']) {
            await page.setContent('<button id=game>Inventory</button><dialog id=first><input id=one value=one></dialog><dialog id=second><input id=two value=two></dialog>');
            check(await page.evaluate(mode=>{game.focus();first.showModal();one.setSelectionRange(3,3);second.showModal();if(mode==='underlying')first.close();if(mode==='disabled')one.disabled=true;if(mode==='hidden')one.style.display='none';second.close();return document.activeElement.id;},mode),mode==='normal'?'one':'two');
            await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
            check(await page.evaluate(()=>document.activeElement.id),mode==='normal'?'one':'');
            await page.keyboard.type('x');
            check(await page.evaluate(()=>[one.value,two.value]),[mode==='normal'?'onex':'one','two']);
        }
        for (const mode of ['display','visibility','override','hidden','opacity']) {
            await page.setContent('<style>#status{width:73px}#panel:focus-within + #status{width:37px}</style><div id=panel><input id=field value=abc></div><aside id=status>Status</aside>');
            await page.evaluate(mode=>{field.focus();field.setSelectionRange(3,3);if(mode==='display')panel.style.display='none';if(mode==='visibility'||mode==='override')panel.style.visibility='hidden';if(mode==='override')field.style.visibility='visible';if(mode==='hidden')panel.hidden=true;if(mode==='opacity')panel.style.opacity='0';},mode);
            await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
            const available=mode==='override'||mode==='opacity';
            check(await page.evaluate(()=>document.activeElement.id),available?'field':'');
            check(await page.$eval('#status',e=>getComputedStyle(e).width),available?'37px':'73px');
            await page.keyboard.type('x');
            check(await page.$eval('#field',e=>e.value),available?'abcx':'abc');
        }
        for (const mode of ['panel','modal','self','popover']) {
            await page.setContent('<div id="panel"><input id="field" value="abc"><dialog id="modal"><input id="inside" value="abc"></dialog><div popover="manual" id="popup"><input id="popfield" value="abc"></div></div>');
            await page.evaluate(()=>{field.focus();panel.inert=true;});
            await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
            check(await page.evaluate(()=>document.activeElement.id),'');
            await page.evaluate(mode=>{if(mode==='modal'||mode==='self'){modal.showModal();inside.focus();inside.setSelectionRange(3,3);if(mode==='self')modal.inert=true;}if(mode==='popover'){popup.showPopover();popfield.focus();}},mode);
            await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
            check(await page.evaluate(()=>document.activeElement.id),mode==='modal'?'inside':'');
            await page.keyboard.type('x');
            check(await page.evaluate(()=>[field.value,inside.value,popfield.value]),['abc',mode==='modal'?'abcx':'abc','abc']);
        }
        for (const modalMode of [false,true]) for (const mode of ['normal','inert','hidden','outside']) {
            await page.setContent('<button id="before">Open</button><button id="outside">Outside</button><dialog id="dialog"><button id="first">First</button><button id="second">Second</button></dialog>');
            check(await page.evaluate(({modalMode,mode})=>{before.focus();if(mode==='inert')first.inert=true;if(mode==='hidden')first.style.display='none';if(modalMode)dialog.showModal();else dialog.show();return document.activeElement.id;},{modalMode,mode}), mode==='inert'||mode==='hidden'?'second':'first');
            check(await page.evaluate(mode=>{if(mode==='outside')outside.focus();dialog.close();return document.activeElement.id;},mode),mode==='outside'&&!modalMode?'outside':'before');
        }
        for (const mode of ['parent','override','self','auto']) {
            await page.setContent('<div id="panel" tabindex="0"><input id="field" value="abc"></div>');
            await page.evaluate(mode=>{field.focus();field.setSelectionRange(3,3);if(mode==='self')field.style.contentVisibility='hidden';else panel.style.contentVisibility=mode==='auto'?'auto':'hidden';if(mode==='override')field.style.contentVisibility='visible';},mode);
            await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
            const available=mode==='self'||mode==='auto';
            check(await page.evaluate(()=>document.activeElement.id),available?'field':'');
            await page.keyboard.type('x');
            check(await page.$eval('#field',e=>e.value),mode==='auto'?'abcx':'abc');
            await page.keyboard.press('Backspace');
            check(await page.$eval('#field',e=>e.value),'abc');
            check(await page.evaluate(()=>{field.focus();return document.activeElement.id;}),available?'field':'');
            check(await page.evaluate(()=>{panel.focus();return document.activeElement.id;}),'panel');
        }
        for (const [kind,markup] of Object.entries({text:'<input value="Hello">',checkbox:'<input type="checkbox" checked>',radio:'<input type="radio" checked>',range:'<input type="range">',select:'<select><option>Hello</option></select>',textarea:'<textarea>Hello</textarea>',button:'<button>Hello</button>'})) {
            await page.setContent('<style>input,select,textarea,button{width:120px;height:40px;box-sizing:border-box;outline:none}</style>'+markup);
            const control=await page.$('input,select,textarea,button');
            const visible=Buffer.from(await control.screenshot());
            await control.evaluate(e=>e.style.contentVisibility='hidden');
            const hidden=Buffer.from(await control.screenshot());
            check(visible.equals(hidden),kind==='checkbox'||kind==='radio');
            await control.evaluate(e=>e.style.contentVisibility='visible');
            check(visible.equals(Buffer.from(await control.screenshot())),true);
        }
        for (const [css,expected] of [['',[100,0,300,20]],['width:350px',[0,100,350,20]],['width:50%',[100,0,200,20]],['margin-left:20px;margin-right:30px',[100,0,270,20]],['width:100px;margin:auto',[200,0,100,20]],['min-width:350px',[0,100,400,20]],['max-width:150px',[100,0,150,20]],['padding:10%;border:2px solid',[100,0,300,104]],['clear:both',[0,100,400,20]],['margin-top:120px',[0,120,400,20]],['aspect-ratio:1;height:auto',[100,0,300,300]],['aspect-ratio:1;height:20px',[100,0,20,20]]]) {
            await page.setContent('<style>body{margin:0}#parent{width:400px;display:flow-root}#avatar{float:left;width:100px;height:100px}#content{display:flow-root;height:20px;'+css+'}</style><div id="parent"><div id="avatar"></div><div id="content"></div></div>');
            check(await page.$eval('#content',e=>{const r=e.getBoundingClientRect();return [r.x,r.y,r.width,r.height];}),expected);
            await page.$eval('#avatar',e=>e.style.width='150px');
            const changed={'':[150,0,250,20],'width:350px':[0,100,350,20],'width:50%':[150,0,200,20],'margin-left:20px;margin-right:30px':[150,0,220,20],'width:100px;margin:auto':[225,0,100,20],'min-width:350px':[0,100,400,20],'max-width:150px':[150,0,150,20],'padding:10%;border:2px solid':[150,0,250,104],'clear:both':[0,100,400,20],'margin-top:120px':[0,120,400,20],'aspect-ratio:1;height:auto':[150,0,250,250],'aspect-ratio:1;height:20px':[150,0,20,20]};
            check(await page.$eval('#content',e=>{const r=e.getBoundingClientRect();return [r.x,r.y,r.width,r.height];}),changed[css]);
        }
        await page.setContent('<style>body{margin:0}#p{width:400px;display:flow-root}#a{float:left;width:100px;height:100px}#b{float:right;clear:left;width:150px;height:200px}#c{display:flow-root;font-size:0;line-height:0}i{display:inline-block;width:180px;height:40px;vertical-align:top}</style><div id="p"><div id="a"></div><div id="b"></div><div id="c"><i id="tile"></i><i></i><i></i><i></i></div></div>');
        for (const height of [100,200,100]) {
            await page.$eval('#a',(e,h)=>e.style.height=h+'px',height);
            check(await page.$eval('#c',e=>{const r=e.getBoundingClientRect();return [r.x,r.y,r.width,r.height];}),[100,0,height===200?300:150,160]);
            check(await page.$eval('#tile',e=>{const r=e.getBoundingClientRect();return [r.x,r.y,r.width,r.height];}),[100,0,180,40]);
        }
        for (const display of ['flow-root','block']) for (const height of [0,20]) {
            await page.setContent('<style>html,body{margin:0}#p{width:400px;display:'+display+'}#f{float:left;width:100px;height:80px}#c{height:'+height+'px;clear:left;margin-top:30px;margin-bottom:10px}#after{height:10px}</style><div id="p"><div id="f"></div><div id="c"></div><div id="after"></div></div>');
            for (const margin of [30,100,-30,30]) {
                await page.$eval('#c',(e,m)=>e.style.marginTop=m+'px',margin);
                const top=display==='flow-root'&&margin===100?100:80;
                const following=height===20?top+30:top+(margin<0?10:0);
                check(await page.$eval('#c',e=>{const r=e.getBoundingClientRect();return [r.x,r.y,r.width,r.height];}),[0,top,400,height]);
                check(await page.$eval('#after',e=>{const r=e.getBoundingClientRect();return [r.x,r.y,r.width,r.height];}),[0,following,400,10]);
            }
        }
        await page.setContent('<input id="a" value="abcdef"><input id="b" value="second"><button id="apply">Apply</button>');
        check(await page.$eval('#a',e=>[e.selectionStart,e.selectionEnd]),[0,0]);
        await page.$eval('#a',e=>e.focus());
        check(await page.$eval('#a',e=>[e.selectionStart,e.selectionEnd]),[0,0]);
        await page.$eval('#b',e=>e.value='replacement');
        check(await page.$eval('#b',e=>[e.selectionStart,e.selectionEnd]),[11,11]);
        await page.$eval('#a',e=>{e.focus();e.setSelectionRange(1,4,'backward');});
        for (const target of ['#b','#a','#apply','#a']) {
            await page.$eval(target,e=>e.focus());
            check(await page.$eval('#a',e=>[e.selectionStart,e.selectionEnd,e.selectionDirection]),[1,4,'backward']);
        }
        await page.$eval('#b',e=>e.focus());
        await page.$eval('#a',e=>e.value='xy');
        check(await page.$eval('#a',e=>[e.selectionStart,e.selectionEnd]),[2,2]);
        await page.$eval('#a',e=>e.focus());
        check(await page.$eval('#a',e=>[e.selectionStart,e.selectionEnd]),[2,2]);
        await page.$eval('#a',e=>e.setSelectionRange(0,1));
        await page.$eval('#b',e=>e.focus());
        await page.$eval('#a',e=>e.value='xy');
        check(await page.$eval('#a',e=>[e.selectionStart,e.selectionEnd]),[0,1]);
        await page.$eval('#a',e=>e.setSelectionRange(0,2,'backward'));
        check(await page.evaluate(()=>document.activeElement.id),'b');
        check(await page.$eval('#a',e=>[e.selectionStart,e.selectionEnd]),[0,2]);
        check(await page.$eval('#a',e=>e.selectionDirection),'backward');
        await page.setContent('<button id=start>Start</button><input id=a value=abcdef><textarea id=t>abcdef</textarea>');
        await page.focus('#start');
        await page.keyboard.press('Tab');
        check(await page.evaluate(()=>{const e=document.activeElement;return [e.id,e.selectionStart,e.selectionEnd];}),['a',0,6]);
        await page.keyboard.press('Tab');
        check(await page.evaluate(()=>{const e=document.activeElement;return [e.id,e.selectionStart,e.selectionEnd];}),['t',0,0]);
        console.log(`Chrome form state: ${checks} checks, 0 failures (${await browser.version()})`);
    } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
