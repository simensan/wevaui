const fs = require('node:fs'), path = require('node:path');
const {createRequire} = require('node:module');
const puppeteer = createRequire(path.resolve(__dirname, '../../../Tools/Layout/package.json'))('puppeteer');
(async () => {
  const browser = await puppeteer.launch({headless:true, executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page = await browser.newPage(), rows = [];
    for (const linked of [false,true]) for (const a of ['auto','hint','manual']) for (const b of ['auto','hint','manual']) for (const c of ['auto','hint','manual']) {
      const child = '<div id=c popover='+c+'>Child</div>';
      const middle = '<div id=b popover='+b+'><button id=bc popovertarget=c>Child</button>'+(linked?'':child)+'</div>';
      const html = '<div id=a popover='+a+'><button id=ab popovertarget=b>Middle</button>'+(linked?'':middle)+'</div>'+(linked?middle+child:'');
      const actions = [['show','a'],[linked?'click':'show',linked?'ab':'b'],[linked?'click':'show',linked?'bc':'c'],['hide','b'],['show','b'],['show','c'],['hide','a']];
      await page.setContent(html);
      const states = await page.evaluate(actions => actions.map(([action,id]) => {
        const e=document.getElementById(id); let error='';
        try {if(action==='click') e.click(); else e[action==='show'?'showPopover':'hidePopover']();} catch(ex) {error=ex.name;}
        return {error,open:Array.from(document.querySelectorAll(':popover-open'),e=>e.id)};
      }), actions);
      rows.push({name:[linked?'linked':'nested',a,b,c].join('-'),html,actions,states});
    }
    fs.writeFileSync(process.argv[2],JSON.stringify({browser:await browser.version(),rows},null,2));
  } finally {await browser.close();}
})();
