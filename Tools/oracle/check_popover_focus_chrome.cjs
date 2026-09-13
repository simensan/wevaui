const fs = require('node:fs'), path = require('node:path');
const {createRequire} = require('node:module');
const puppeteer = createRequire(path.resolve(__dirname, '../../Tools/Layout/package.json'))('puppeteer');
(async () => {
  const browser = await puppeteer.launch({headless:true, executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page = await browser.newPage(), rows = [];
    for (const kind of ['auto','hint','manual']) for (const tag of ['div','dialog']) for (const autofocus of [false,true]) for (const moveOutside of [false,true]) {
      const html = '<button id=outside>Open</button><button id=other>Other</button><'+tag+' id=p popover='+kind+'><input id=field value=abcdef '+(autofocus?'autofocus':'')+'></'+tag+'>';
      await page.setContent(html);
      const states = await page.evaluate(moveOutside => {
        const p=document.getElementById('p'); document.getElementById('outside').focus();
        p.showPopover(); const shown=document.activeElement.id;
        if(moveOutside) document.getElementById('other').focus();
        p.hidePopover(); const hidden=document.activeElement.id;
        p.getBoundingClientRect();
        const hiddenAfterLayout=document.activeElement.id;
        return {shown,hidden,hiddenAfterLayout};
      },moveOutside);
      const hiddenAfterFrame = await page.evaluate(async () => {await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))); return document.activeElement.id;});
      rows.push({kind,tag,autofocus,moveOutside,html,...states,hiddenAfterFrame});
    }
    const transitions = [];
    for (const nested of [false,true]) for (const first of ['auto','hint','manual']) for (const second of ['auto','hint','manual']) for (const autofocus of [false,true]) {
      const child='<div id=b popover='+second+'><input id=second '+(autofocus?'autofocus':'')+'></div>';
      const html='<button id=outside>Outside</button><div id=a popover='+first+'><input id=first autofocus>'+(nested?child:'')+'</div>'+(nested?'':child);
      await page.setContent(html);
      await page.evaluate(()=>document.getElementById('outside').focus());
      const states=[];
      const actions=[['show','a'],['show','b'],['hide','b'],['hide','a']];
      for(const [action,id] of actions) {
        const immediate=await page.evaluate(({action,id})=>{document.getElementById(id)[action==='show'?'showPopover':'hidePopover']();return document.activeElement.id;},{action,id});
        const settled=await page.evaluate(async()=>{await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));return document.activeElement.id;});
        states.push({immediate,settled});
      }
      transitions.push({name:[nested?'nested':'sibling',first,second,autofocus].join('-'),html,actions,states});
    }
    fs.writeFileSync(process.argv[2],JSON.stringify({browser:await browser.version(),rows,transitions},null,2));
  } finally {await browser.close();}
})();
