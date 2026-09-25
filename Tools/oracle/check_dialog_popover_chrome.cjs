const fs = require('node:fs'), path = require('node:path');
const {launch} = require('./chrome_test_browser.cjs');
(async () => {
  const browser = await launch({headless:true, executablePath:undefined});
  try {
    const page = await browser.newPage(), rows = [];
    for (const kind of ['auto','manual','hint']) for (const actions of [['showPopover','show'],['showPopover','showModal'],['show','showPopover'],['showModal','showPopover'],['showPopover','show','close']]) {
      await page.setContent('<dialog id=d popover='+kind+'><button id=b>Button</button></dialog>');
      const states = await page.evaluate(actions => actions.map(action => {
        const d = document.querySelector('#d'); let error = '';
        try {d[action]();} catch(e) {error = e.name;}
        return {action,error,open:d.open,popover:d.matches(':popover-open'),modal:d.matches(':modal')};
      }), actions);
      rows.push({kind,actions,states});
    }
    const siblings = [];
    for (const first of ['auto','manual','hint']) for (const second of ['auto','manual','hint']) {
      await page.setContent('<div id=a popover='+first+'>A</div><div id=b popover='+second+'>B</div>');
      const open = await page.evaluate(() => {document.querySelector('#a').showPopover(); document.querySelector('#b').showPopover(); return Array.from(document.querySelectorAll(':popover-open'), e=>e.id);});
      siblings.push({first,second,open});
    }
    const nested = [];
    for (const linked of [false,true]) for (const first of ['auto','manual','hint']) for (const second of ['auto','manual','hint']) {
      const child = '<div id=b popover='+second+'>B</div>';
      await page.setContent('<div id=a popover='+first+'><button id=trigger popovertarget=b>Child</button>'+(linked?'':child)+'</div>'+(linked?child:''));
      const open = await page.evaluate(linked => {document.querySelector('#a').showPopover(); if(linked) document.querySelector('#trigger').click(); else document.querySelector('#b').showPopover(); return Array.from(document.querySelectorAll(':popover-open'),e=>e.id);},linked);
      nested.push({linked,first,second,open});
    }
    const dismiss = [];
    for (const linked of [false,true]) for (const first of ['auto','manual','hint']) for (const second of ['auto','manual','hint']) for (const action of ['hide','remove-attribute','change-manual']) {
      const child = '<div id=b popover='+second+'>B</div>';
      await page.setContent('<div id=a popover='+first+'><button id=trigger popovertarget=b>Child</button>'+(linked?'':child)+'</div>'+(linked?child:''));
      const open = await page.evaluate(({linked,action}) => {
        const a=document.querySelector('#a'); a.showPopover();
        if(linked) document.querySelector('#trigger').click(); else document.querySelector('#b').showPopover();
        if(action==='hide') a.hidePopover(); else if(action==='remove-attribute') a.removeAttribute('popover'); else a.setAttribute('popover','manual');
        return Array.from(document.querySelectorAll(':popover-open'),e=>e.id);
      },{linked,action});
      dismiss.push({linked,first,second,action,open});
    }
    fs.writeFileSync(process.argv[2], JSON.stringify({browser:await browser.version(),rows,siblings,nested,dismiss},null,2));
  } finally {await browser.close();}
})();
