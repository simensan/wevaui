const fs=require('fs'),path=require('path');
const {createRequire}=require('module');
const puppeteer=createRequire(path.resolve('Tools/Layout/package.json'))('puppeteer');
(async()=>{
 const browser=await puppeteer.launch({headless:true,executablePath:process.env.PUPPETEER_EXECUTABLE_PATH||'C:/Program Files/Google/Chrome/Application/chrome.exe'});
 try {
  const page=await browser.newPage(),rows=[];
  const cases=[{}, {value:'5'}, {value:'0',min:'0',max:'10'}, {value:'10',min:'0',max:'10'},
   {value:'3',min:'0',max:'10',step:'2'}, {value:'-5',min:'0',max:'10'}, {value:'20',min:'0',max:'10'},
   {min:'5',max:'10'}, {min:'-10',max:'-5'}, {value:'3',min:'10',max:'0'},
   {value:'0.3',step:'0.1'}, {value:'-0.3',step:'0.1'}, {value:'1.2',step:'any'},
   {value:'5',step:'0'}, {value:'5',step:'-2'}, {value:'5',step:'garbage'},
   {value:'0.5',current:'1',step:'1'}, {value:'4',min:'1',step:'2'},
   {value:'5',readonly:''}, {value:'5',disabled:''}, {value:'1e2',step:'1e1'}, {value:'',max:'0.5'},
   {min:'10',max:'0'}, {value:'0.3',min:'0',step:'0.1'}, {value:'0.3',min:'0',step:'any'},
   {value:'0.5',min:'0.5',max:'0.6',step:'1'}, {value:'9',min:'0',max:'9',step:'2'},
   {value:'20',min:'1',max:'10',step:'2'}, {min:'1',max:'10',step:'2'},
   {value:'-0.1',step:'0.1'}, {value:'1e-7',step:'1e-7'},
   {value:'0.123456789012345',step:'0.000000000000001'},
   {value:'9007199254740992'}, {value:'1e308',step:'1e308'},
   {value:'0',min:'0',step:'1e-20'}, {value:'0.0',min:'0'},
   {value:'10.0',max:'10'}, {value:'-1e308',step:'1e308'}];
  for(const attrs of cases) for(const key of ['ArrowUp','ArrowDown']) {
   await page.setContent('<input id=n type=number>');
   await page.$eval('#n',(e,attrs)=>{for(const [k,v]of Object.entries(attrs))if(k!=='current')e.setAttribute(k,v);if('current'in attrs)e.value=attrs.current;window.events=[];for(const name of ['input','change'])e.addEventListener(name,()=>window.events.push(name));e.focus()},attrs);
   const before=await page.$eval('#n',e=>e.value);
   await page.keyboard.press(key);
   const after=await page.$eval('#n',e=>({value:e.value,events:window.events}));
   rows.push({attrs,key,before,...after});
  }
  const result={browser:await browser.version(),rows};
  if(process.argv[2])fs.writeFileSync(process.argv[2],JSON.stringify(result,null,2));
  console.log(JSON.stringify(result));
 } finally {await browser.close()}
})();
