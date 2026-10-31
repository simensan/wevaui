// Expectations shared with test_cascade_order and test_revert.
const assert = require('node:assert/strict');
const {launch} = require('./chrome_test_browser.cjs');

(async () => {
    const browser = await launch({headless: true});
    try {
        const page = await browser.newPage();
        const rows = await page.evaluate(() => {
            const rows = [];
            const build = css => {
                document.body.innerHTML = '<style>html{color-scheme:light}' + css +
                    '</style><div id=p><div id=x></div></div>';
            };
            for (const keyword of ['inherit', 'initial', 'unset', 'revert', 'revert-layer']) {
                build(`#p{color:red}#x{color:blue;padding-left:13px;font-size:32px;line-height:40px}` +
                    `#x::before{content:'x';color:${keyword};padding-left:${keyword};` +
                    `font-size:${keyword};line-height:${keyword}}`);
                const style = getComputedStyle(document.getElementById('x'), '::before');
                rows.push({label:keyword, color:style.color, padding:style.paddingLeft,
                    font:style.fontSize, line:style.lineHeight});
                build(`#p{color:blue}#x{color:${keyword}}`);
                rows.push({label:'element '+keyword,
                    color:getComputedStyle(document.getElementById('x')).color});
            }
            build('#x{color:blue}@layer first,second;' +
                '@layer first{#x::before{content:"x";color:green;font-size:24px;line-height:30px}}' +
                '@layer second{#x::before{color:revert-layer;font-size:revert-layer;line-height:revert-layer}}');
            const style = getComputedStyle(document.getElementById('x'), '::before');
            rows.push({label:'layer rollback',color:style.color,font:style.fontSize,line:style.lineHeight});
            build('#x{color:blue;padding-left:13px}#x::before{content:"x";' +
                'color:var(--missing, inherit);padding-left:var(--missing, inherit)}');
            const variable = getComputedStyle(document.getElementById('x'), '::before');
            rows.push({label:'variable fallback',color:variable.color,padding:variable.paddingLeft});
            build('#p{color:rgba(255,0,0,.25)}#x{color:currentColor;background-color:CuRrEnTcOlOr}' +
                '#x::before{content:"x";color:inherit;background-color:currentColor}');
            for (const color of ['rgba(255, 0, 0, 0.25)', 'rgb(0, 255, 0)', 'rgb(0, 0, 255)']) {
                document.getElementById('p').style.color = color;
                const x = document.getElementById('x');
                rows.push({label:'currentColor '+color, color:getComputedStyle(x).color,
                    background:getComputedStyle(x).backgroundColor,
                    pseudo:getComputedStyle(x, '::before').backgroundColor});
            }
            return rows;
        });
        for (const keyword of ['inherit', 'initial', 'unset', 'revert', 'revert-layer']) {
            const initial = keyword === 'initial';
            assert.deepEqual(rows.find(r => r.label === keyword), {
                label:keyword, color:initial ? 'rgb(0, 0, 0)' : 'rgb(0, 0, 255)',
                padding:keyword === 'inherit' ? '13px' : '0px',
                font:initial ? '16px' : '32px', line:initial ? 'normal' : '40px'});
            assert.deepEqual(rows.find(r => r.label === 'element '+keyword), {
                label:'element '+keyword, color:initial ? 'rgb(0, 0, 0)' : 'rgb(0, 0, 255)'});
        }
        assert.deepEqual(rows.find(r => r.label === 'layer rollback'), {
            label:'layer rollback',color:'rgb(0, 128, 0)',font:'24px',line:'30px'});
        assert.deepEqual(rows.find(r => r.label === 'variable fallback'), {
            label:'variable fallback',color:'rgb(0, 0, 255)',padding:'13px'});
        for (const color of ['rgba(255, 0, 0, 0.25)', 'rgb(0, 255, 0)', 'rgb(0, 0, 255)'])
            assert.deepEqual(rows.find(r => r.label === 'currentColor '+color), {
                label:'currentColor '+color,color,background:color,pseudo:color});
        console.log(JSON.stringify({browser:await browser.version(),rows},null,2));
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
