// Browser evidence for maxlength and replacement behavior. execCommand drives
// the editor's text insertion without CDP insertText's extra caret relocation.
const {launch} = require('./chrome_test_browser.cjs');
const assert = require('node:assert/strict');

(async () => {
    const browser = await launch({headless:true, executablePath:process.argv[2]});
    let checks = 0;
    const check = (actual, expected) => { assert.deepEqual(actual, expected); ++checks; };
    try {
        const page = await browser.newPage(), cdp = await page.createCDPSession();
        const state = () => page.$eval('#field', f => ({value:f.value, selection:[f.selectionStart,f.selectionEnd]}));
        const value = async () => (await state()).value;
        const events = () => page.evaluate(() => window.events.splice(0));
        const insert = text => page.evaluate(t => document.execCommand('insertText', false, t), text);
        const undo = async () => { await page.keyboard.down('Control'); await page.keyboard.press('z'); await page.keyboard.up('Control'); };
        const preview = text => cdp.send('Input.imeSetComposition',{text,selectionStart:text.length,selectionEnd:text.length});
        const reset = async (tag, limit, text='', from=text.length, to=from) => {
            await page.setContent(`<${tag} id=field></${tag}><button id=other>Other</button>`);
            await page.$eval('#field',(f,s) => {
                f.setAttribute('maxlength',s.limit); f.value=s.text; f.focus(); f.setSelectionRange(s.from,s.to);
                window.events=[];
                for(const kind of ['input','compositionupdate','compositionend'])
                    f.addEventListener(kind,e=>events.push([kind,e.data]));
            },{limit,text,from,to});
        };
        for(const tag of ['input','textarea']) {
            for(const [limit,before,from,to,text,after,caret] of [
                [3,'',0,0,'abcdef','abc',3], [1,'',0,0,'😀a','',0], [2,'',0,0,'😀a','😀',2],
                [1,'',0,0,'á','a',1], [3,'abc',3,3,'x','abc',3], [3,'abc',1,2,'XYZ','aXc',2],
                [3,'abcdef',2,4,'z','abef',2], [3,'abcdef',0,6,'XYZW','XYZ',3],
                [3,'abcdef',1,5,'XY','aXf',2], [0,'',0,0,'x','',0],
                [4,'a😀b',1,3,'界😀','a界b',2], [4,'a😀b',3,3,'x','a😀b',3]
            ]) {
                await reset(tag,limit,before,from,to); await insert(text);
                check(await state(),{value:after,selection:[caret,caret]});
                const recorded = await events();
                check(recorded, from===to && before===after ? [] : [['input',after.slice(from,caret)]]);
                await undo(); check(await value(),before);
            }
            for(const limit of ['3x','3.5','+3',' \t3','003','3e5']) {
                await reset(tag,limit); await insert('abcde'); check(await value(),'abc');
            }
            for(const limit of ['', '-1','2147483648','99999999999999999999','x3','+-3']) {
                await reset(tag,limit); await insert('abcde'); check(await value(),'abcde');
            }
            await reset(tag,'-0'); await insert('abc'); check(await value(),'');
            await reset(tag,5); await insert('\nb\rc\r\nd');
            check(await value(),tag==='textarea'?'\nb\nc\n':' b c ');
            await reset(tag,20); await insert('abc\n\r\n');
            check(await value(),tag==='textarea'?'abc\n\n':'abc');
            await reset(tag,3,'abc',1,2); await page.keyboard.press('Enter');
            check(await state(),{value:tag==='textarea'?'a\nc':'abc',selection:tag==='textarea'?[2,2]:[1,2]});
            await reset(tag,3,'abcdef',2,4); await page.keyboard.press('Enter');
            check(await state(),{value:'abcdef',selection:[2,4]});
            await reset(tag,3,'abc'); await page.keyboard.press('Enter'); check(await value(),'abc');
            for(const text of ['日本','😀']) {
                await reset(tag,1); await preview(text); check(await value(),text); await events();
                await cdp.send('Input.insertText',{text});
                const accepted=text==='日本'?'日':'';
                check(await value(),accepted);
                check(await events(),[['compositionupdate',text],['input',accepted],['compositionend',text]]);
                await undo(); check(await value(),'');
            }
            await reset(tag,3,'abc',1,2); await preview('日本語'); check(await value(),'a日本語c');
            await cdp.send('Input.insertText',{text:'日本語'}); check(await value(),'a日c');
            await undo(); check(await value(),'abc');
            await reset(tag,3); await preview('日本語'); await page.$eval('#field',f=>f.maxLength=1);
            await page.focus('#other'); check(await value(),'日');
            await reset(tag,1); await preview('日本'); await page.$eval('#field',f=>f.disabled=true);
            check(await value(),'日本');
            await reset(tag,0,'programmatic'); check(await value(),'programmatic');
            await page.$eval('#field',f=>f.removeAttribute('maxlength')); await insert('!'); check(await value(),'programmatic!');
            await reset(tag,3); await page.keyboard.type('abc'); await events();
            await page.keyboard.type('x'); check(await events(),[]); await undo(); check(await value(),'');
        }
        for(const type of ['text','password','search','email','url','tel','TEXT','number']) {
            await page.setContent(`<input id=field type=${type} maxlength=2>`); await page.focus('#field');
            await insert('1234'); check(await value(),type==='number'?'1234':'12');
        }
        console.log(`Chrome maxlength: ${checks} checks, 0 failures (${await browser.version()})`);
    } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
