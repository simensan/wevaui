// Browser counterparts of test_c_abi_text_editing.cpp. Selection offsets in
// Chrome are UTF-16 code units; the core API uses UTF-8 bytes.
const {launch} = require('./chrome_test_browser.cjs');
const assert = require('node:assert/strict');

(async () => {
    const browser = await launch({headless: true, executablePath: process.argv[2]});
    let checks = 0;
    const check = (got, want) => { assert.deepEqual(got, want); ++checks; };
    try {
        const page = await browser.newPage();
        for (const inlineStyle of ['font-size:40px', 'letter-spacing:3px', 'word-spacing:5px']) {
            await page.setContent('<div id=block style="font:20px monospace;white-space:pre;tab-size:4"><span style="' + inlineStyle + '">\t<span id=after>X</span></span></div>');
            const widths = await page.$eval('#block', block => {
                const c = document.createElement('canvas').getContext('2d');
                c.font = getComputedStyle(block).font;
                return [document.querySelector('#after').getBoundingClientRect().x - block.getBoundingClientRect().x,
                    c.measureText(' ').width * 4];
            });
            check(Math.abs(widths[0] - widths[1]) < 1 / 64, true);
        }
        await page.setContent('<input id=f><textarea id=t></textarea>');
        await page.$eval('#t', f => {
            f.style.cssText='font:20px monospace;width:4ch;height:200px;padding:0;border:0;word-break:break-all';
            f.value='abcdefgh\nijkl\nmnop'; f.focus(); f.setSelectionRange(1,1);
        });
        for (const [key, expected] of [['ArrowDown',10],['ArrowUp',5],['ArrowUp',0]]) {
            await page.keyboard.down('Control'); await page.keyboard.press(key); await page.keyboard.up('Control');
            check(await page.$eval('#t', f=>[f.selectionStart,f.selectionEnd]), [expected,expected]);
        }
        await page.$eval('#t', f=>f.setSelectionRange(5,5));
        await page.keyboard.down('Control'); await page.keyboard.down('Shift');
        await page.keyboard.press('ArrowDown');
        await page.keyboard.up('Shift'); await page.keyboard.up('Control');
        check(await page.$eval('#t', f=>[f.selectionStart,f.selectionEnd]), [5,10]);
        await page.$eval('#t', f=>{f.value='abc\n\ndef';f.setSelectionRange(2,2);});
        for (const [key, expected] of [['ArrowDown',4],['ArrowDown',7],['ArrowUp',4],['ArrowUp',2]]) {
            await page.keyboard.down('Control'); await page.keyboard.press(key); await page.keyboard.up('Control');
            check(await page.$eval('#t', f=>[f.selectionStart,f.selectionEnd]), [expected,expected]);
        }
        for (const wrap of ['soft', 'off', 'OFF', 'hard', 'unknown']) {
            await page.$eval('#t', (f, wrap) => {
                f.style.cssText = 'font:20px monospace;padding:0;border:0;height:200px';
                const c = document.createElement('canvas').getContext('2d');
                c.font = getComputedStyle(f).font;
                f.style.width = c.measureText('a').width * 3.2 + 'px';
                f.setAttribute('wrap', wrap); f.value = 'aaaaaaaaa'; f.focus(); f.setSelectionRange(1, 1);
            }, wrap);
            await page.keyboard.press('ArrowDown');
            const expected = wrap.toLowerCase() === 'off' ? 9 : 4;
            check(await page.$eval('#t', f => [f.selectionStart, f.selectionEnd]), [expected, expected]);
        }
        await page.$eval('#t', f => f.removeAttribute('wrap'));
        for (const whitespace of ['pre', 'pre-wrap']) for (const zero of ['0', '0px', '0em']) {
            const advance = await page.$eval('#t', (f, {whitespace, zero}) => {
                f.style.cssText = 'font:20px monospace;white-space:' + whitespace + ';tab-size:' + zero;
                f.value = 'a\t\t'; f.focus(); f.setSelectionRange(3,3);
                const d = document.createElement('div');
                d.style.cssText = f.style.cssText + ';position:absolute'; d.textContent = '\tX';
                document.body.appendChild(d);
                const range = document.createRange(); range.setStart(d.firstChild,1); range.setEnd(d.firstChild,2);
                const x = range.getBoundingClientRect().x - d.getBoundingClientRect().x;
                d.remove(); return x;
            }, {whitespace, zero});
            check(advance, 0);
            await page.keyboard.press('ArrowLeft');
            check(await page.$eval('#t', f => [f.selectionStart, f.selectionEnd]), [2,2]);
        }
        for (const whitespace of ['pre', 'pre-wrap']) {
            const origin = await page.$eval('#t', (f, whitespace) => {
                f.style.cssText = 'font:20px monospace;line-height:24px;width:400px;height:200px;padding:0;border:0;tab-size:25px;white-space:' + whitespace;
                f.value = '\t\t'; f.focus();
                const d = document.createElement('div'); d.style.cssText = f.style.cssText;
                d.textContent = '\t\tX'; document.body.append(d);
                const range = document.createRange(); range.setStart(d.firstChild,2); range.setEnd(d.firstChild,3);
                const advance = range.getBoundingClientRect().x - d.getBoundingClientRect().x;
                d.remove(); const r = f.getBoundingClientRect(); return {x:r.x, y:r.y, advance};
            }, whitespace);
            check(origin.advance, 50);
            for (const [x, offset] of [[30,1], [45,2]]) {
                await page.mouse.click(origin.x + x, origin.y + 12);
                check(await page.$eval('#t', f => [f.selectionStart,f.selectionEnd]), [offset,offset]);
            }
            const points = await page.$eval('#t', (f, whitespace) => {
                f.style.cssText = 'font:20px monospace;line-height:24px;width:400px;height:200px;padding:0;border:0;tab-size:4;white-space:' + whitespace;
                f.value = 'a\tb'; f.focus();
                const c = document.createElement('canvas').getContext('2d');
                c.font = getComputedStyle(f).font;
                const w = c.measureText('a').width, r = f.getBoundingClientRect();
                return [.2, .8].map(t => ({x:r.x + w + t * w * 3, y:r.y + 12}));
            }, whitespace);
            for (let i = 0; i < points.length; ++i) {
                await page.mouse.click(points[i].x, points[i].y);
                check(await page.$eval('#t', f => [f.selectionStart, f.selectionEnd]), [i + 1, i + 1]);
            }
            await page.$eval('#t', f => {f.value='a\t\na\t'; f.setSelectionRange(2,2);});
            await page.keyboard.press('ArrowDown');
            check(await page.$eval('#t', f => [f.selectionStart, f.selectionEnd]), [5,5]);
            await page.keyboard.press('ArrowUp');
            check(await page.$eval('#t', f => [f.selectionStart, f.selectionEnd]), [2,2]);
        }
        for (const mode of ['uppercase', 'lowercase', 'capitalize']) {
            const point = await page.$eval('#t', (f, mode) => {
                f.style.cssText = 'font:20px monospace;line-height:24px;width:400px;height:200px;padding:0;border:0;text-transform:' + mode;
                f.value = 'ab cd\nef gh'; f.focus(); f.setSelectionRange(1, 1);
                const c = document.createElement('canvas').getContext('2d');
                c.font = getComputedStyle(f).font;
                const r = f.getBoundingClientRect();
                return {x:r.x + c.measureText('aa').width + .1, y:r.y + 36};
            }, mode);
            await page.keyboard.press('ArrowDown');
            check(await page.$eval('#t', f => [f.selectionStart, f.selectionEnd]), [7, 7]);
            await page.mouse.click(point.x, point.y);
            check(await page.$eval('#t', f => [f.selectionStart, f.selectionEnd]), [8, 8]);
            check(await page.$eval('#t', f => f.value), 'ab cd\nef gh');
        }
        for (const selector of ['#f', '#t']) {
            for (const direction of ['forward', 'backward']) {
                for (const [key, expected] of [['ArrowLeft', 2], ['ArrowRight', 6]]) {
                    await page.$eval(selector, (f, direction) => {
                        f.value = 'abcdefghij'; f.focus(); f.setSelectionRange(2, 6, direction);
                    }, direction);
                    await page.keyboard.press(key);
                    check(await page.$eval(selector, f => [f.selectionStart, f.selectionEnd]), [expected, expected]);
                }
            }
        }
        const navigation = async (text, offset, steps, wrap = false) => {
            await page.$eval('#t', (f, {text, offset, wrap}) => {
                f.style.cssText = 'font:20px monospace;padding:0;border:0;height:200px;white-space:pre-wrap;word-break:break-all';
                const context = document.createElement('canvas').getContext('2d');
                context.font = getComputedStyle(f).font;
                f.style.width = (wrap ? context.measureText('a').width * 3.2 : 400) + 'px';
                f.value = text; f.focus(); f.setSelectionRange(offset, offset);
            }, {text, offset, wrap});
            for (const [key, expected] of steps) {
                const shifted = key.startsWith('Shift+');
                if (shifted) await page.keyboard.down('Shift');
                await page.keyboard.press(shifted ? key.slice(6) : key);
                if (shifted) await page.keyboard.up('Shift');
                check(await page.$eval('#t', f => [f.selectionStart, f.selectionEnd]),
                    Array.isArray(expected) ? expected : [expected, expected]);
            }
        };
        await navigation('abcdef\nx\nabcdef', 4,
            [['ArrowDown', 8], ['ArrowDown', 13], ['ArrowUp', 8], ['ArrowUp', 4]]);
        for (const [text, expected, line, column] of [['ab cd ef', 7, 0, 7], ['ab\n\ncd', 3, 1, 0]]) {
            const point = await page.$eval('#t', (f, {text, line, column}) => {
                f.style.cssText = 'font:20px monospace;line-height:24px;padding:0;border:0;width:400px;height:200px';
                f.value = text;
                const c = document.createElement('canvas').getContext('2d');
                c.font = getComputedStyle(f).font;
                const r = f.getBoundingClientRect();
                return {x: r.x + column * c.measureText('a').width + 0.1, y: r.y + line * 24 + 12};
            }, {text, line, column});
            await page.mouse.click(point.x, point.y);
            check(await page.$eval('#t', f => [f.selectionStart, f.selectionEnd]), [expected, expected]);
        }
        await navigation('abcdef\nx\nabcdef', 4,
            [['ArrowDown', 8], ['ArrowLeft', 7], ['ArrowDown', 9]]);
        await navigation('abcdef\nx\nabcdef', 4,
            [['Shift+ArrowDown', [4, 8]], ['Shift+ArrowDown', [4, 13]]]);
        await navigation('abcd\n\nabcd\n', 3,
            [['ArrowDown', 5], ['ArrowDown', 9], ['ArrowDown', 11], ['ArrowUp', 9], ['Home', 6], ['End', 10]]);
        await navigation('abcdefghi', 1,
            [['ArrowDown', 4], ['ArrowDown', 7], ['ArrowUp', 4], ['Home', 3], ['End', 6], ['ArrowDown', 9]], true);
        await page.$eval('#t', f => {
            f.style.wordBreak = 'normal'; f.style.overflowWrap = 'break-word';
            f.setSelectionRange(1, 1);
        });
        for (const expected of [4, 7]) {
            await page.keyboard.press('ArrowDown');
            check(await page.$eval('#t', f => [f.selectionStart, f.selectionEnd]), [expected, expected]);
        }
        await page.$eval('#t', f => {
            f.style.width = '1px'; f.value = 'a\u0301a\u0301a\u0301';
            f.focus(); f.setSelectionRange(0, 0);
        });
        for (const expected of [2, 4]) {
            await page.keyboard.press('ArrowDown');
            check(await page.$eval('#t', f => [f.selectionStart, f.selectionEnd]), [expected, expected]);
        }
        const cases = [
            ['a\u0301', 'a'], ['a\u0301\u0327', 'a\u0301'], ['각', '가'],
            ['😀', ''], ['界', ''], ['👨‍👩‍👧‍👦', ''], ['👩🏽‍💻', ''],
            ['🇸🇪', ''], ['🇸🇪🇫', '🇸🇪'], ['🇸🇪🇫🇷', '🇸🇪'],
            ['👍🏽', ''], ['a🏽', 'a'], ['1️⃣', ''], ['#⃣', ''], ['a⃣', 'a'],
            ['❤️', ''], ['a\uFE0F', ''], ['a\u0301\uFE0F', 'a\u0301'],
            ['a\uFE0F\uFE0F', 'a\uFE0F'], ['a‍😀', 'a‍'],
            ['🏴\u{E0067}\u{E0062}\u{E0065}\u{E006E}\u{E0067}\u{E007F}', ''],
        ];
        for (const selector of ['#f', '#t']) {
            for (const [value, remaining] of cases) {
                await page.$eval(selector, (field, value) => {
                    field.value = value; field.focus(); field.setSelectionRange(value.length, value.length);
                }, value);
                await page.keyboard.press('Backspace');
                check(await page.$eval(selector, f => f.value), remaining);
            }
            for (const value of ['a\u0301', '각', '👨‍👩‍👧‍👦', '👩🏽‍💻', '🇸🇪', '1️⃣']) {
                await page.$eval(selector, (f, value) => {
                    f.value = value; f.focus(); f.setSelectionRange(value.length, value.length);
                }, value);
                await page.keyboard.press('ArrowLeft');
                check(await page.$eval(selector, f => f.selectionStart), 0);
                await page.keyboard.press('ArrowRight');
                check(await page.$eval(selector, f => f.selectionStart), value.length);
                await page.keyboard.press('Home'); await page.keyboard.press('Delete');
                check(await page.$eval(selector, f => f.value), '');
            }
        }
        await page.setContent('<input id=f type=password style="width:1px;padding:0;border:0;font:40px monospace">');
        const width = async value => page.$eval('#f', (f, value) => {f.value=value; return f.scrollWidth;}, value);
        const bullet = await width('a');
        for (const value of ['😀', '界', 'a\u0301', '👨‍👩‍👧‍👦', '🇸🇪', '각']) check(await width(value), bullet);
        check(await width('😀界a'), bullet * 3);
        // Blink editing predates UAX #29's GB9c Indic conjunct rule. Record
        // that distinction explicitly instead of calling all segmentation equal.
        check(await width('क्‍ष'), bullet);
        await page.$eval('#f', f => {f.type='text'; f.value='क्‍ष'; f.focus(); f.setSelectionRange(4,4);});
        await page.keyboard.press('ArrowLeft');
        check(await page.$eval('#f', f => f.selectionStart), 3);
        await page.keyboard.press('Home'); await page.keyboard.press('Delete');
        check(await page.$eval('#f', f => f.value), 'ष');
        await page.setContent('<input id=f style="width:100px;padding:0;border:0;font:20px monospace">');
        await page.$eval('#f', f => {f.value='abcdefghijklmnopqrstuvwxyz'; f.focus(); f.setSelectionRange(0,0);});
        await page.keyboard.press('End');
        check(await page.$eval('#f', f => f.scrollLeft > 0), true);
        await page.keyboard.press('Home');
        check(await page.$eval('#f', f => f.scrollLeft), 0);
        await page.keyboard.press('End'); await page.$eval('#f', f => f.blur());
        check(await page.$eval('#f', f => f.scrollLeft), 0);
        console.log(`Chrome ${await browser.version()}: ${checks} text editing checks, 0 failures`);
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
