const fs=require('node:fs');const {launch} = require('./chrome_test_browser.cjs');
(async()=>{const browser=await launch({headless:true,executablePath:process.argv[3]||undefined});try{
 const page=await browser.newPage();const inputs=[];
 for(const type of ['text','number','email','url','checkbox','radio','range','date','time','month','week','datetime-local','hidden','button','submit','reset','file','color'])for(const mode of ['plain','required','disabled','readonly','custom']){
  inputs.push({name:type+'/'+mode,html:`<form id=f><fieldset id=s><input id=c type="${type}" ${mode==='required'?'required':''} ${mode==='disabled'?'required disabled':''} ${mode==='readonly'?'required readonly':''}></fieldset></form>`,custom:mode==='custom'?'c':null});
 }
 inputs.push(...[
 ['empty','<form id=f><fieldset id=s></fieldset></form>'],
 ['external-owner','<form id=f></form><fieldset id=s><input id=c required form=f></fieldset>'],
 ['other-owner','<form id=other></form><form id=f><fieldset id=s><input id=c required form=other></fieldset></form>'],
 ['fieldset-disabled','<form id=f><fieldset id=s disabled><input id=c required></fieldset></form>'],
 ['first-legend','<form id=f><fieldset id=s disabled><legend><input id=c required></legend></fieldset></form>'],
 ['datalist','<form id=f><fieldset id=s><datalist><input id=c required></datalist></fieldset></form>'],
 ['readonly-parent','<form id=f readonly><fieldset id=s readonly><input id=c required></fieldset></form>'],
 ['fieldset-custom','<form id=f><fieldset id=s><input id=c></fieldset></form>','s'],
 ['button-custom','<form id=f><fieldset id=s><button id=c>Save</button></fieldset></form>','c'],
 ['textarea','<form id=f><fieldset id=s><textarea id=c required></textarea></fieldset></form>'],
 ['select','<form id=f><fieldset id=s><select id=c required><option value="">Choose</option></select></fieldset></form>'],
 ['number-step','<form id=f><fieldset id=s><input id=c type=number min=2 step=2 value=5></fieldset></form>'],
 ['pattern','<form id=f><fieldset id=s><input id=c pattern="[a-z]+" value="123"></fieldset></form>'],
 ['novalidate','<form id=f novalidate><fieldset id=s><input id=c required></fieldset></form>'],
 ['radio-group','<form id=f><fieldset id=s><input id=c type=radio name=g><input type=radio name=g required></fieldset></form>']
 ].map(([name,html,custom])=>({name,html,custom:custom||null})));
 const rows=[];for(const input of inputs){await page.setContent(input.html);rows.push(await page.evaluate(input=>{if(input.custom)document.getElementById(input.custom).setCustomValidity('Reserved');return{...input,states:['f','s','c'].filter(id=>document.getElementById(id)).map(id=>{const e=document.getElementById(id);return{id,valid:e.matches(':valid'),invalid:e.matches(':invalid'),will_validate:e.willValidate??null,validity:e.validity?.valid??null};})};},input));}
 fs.writeFileSync(process.argv[2]||'validity-selector-chrome.json',JSON.stringify({browser:await browser.version(),rows},null,2));console.log(rows.length+' validity scenarios captured');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
