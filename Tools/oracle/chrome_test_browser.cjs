// One Puppeteer dependency and browser policy for every scripted oracle.
// The runner supplies WEVA_CHROME; direct calls may still pass an explicit
// executablePath or use the browser installed by the root package-lock.json.
const puppeteer = require('puppeteer');

function launch(options = {}) {
    const args = [...(options.args || [])];
    if (process.env.WEVA_CHROME_NO_SANDBOX === '1' && !args.includes('--no-sandbox'))
        args.push('--no-sandbox');
    return puppeteer.launch({...options,
        executablePath: process.env.WEVA_CHROME || options.executablePath,
        args});
}

module.exports = {launch};

// The Python oracles build HTML fixtures, then use this same launcher to
// capture their DOM. Chrome's direct --dump-dom startup with a fresh profile
// hangs on some Linux builds; Puppeteer also owns and closes its private
// profile, so temporary-directory cleanup cannot race a live browser.
if (require.main === module) {
    (async () => {
        const browser = await launch({headless: true, executablePath: process.argv[2],
            args: process.argv.includes('--no-sandbox') ? ['--no-sandbox'] : []});
        try {
            const page = await browser.newPage();
            await page.goto(process.argv[3], {waitUntil: 'load'});
            console.log(await page.content());
        } finally {
            await browser.close();
        }
    })().catch(error => { console.error(error); process.exitCode = 1; });
}
