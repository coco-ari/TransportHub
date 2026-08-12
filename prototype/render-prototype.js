const { chromium } = require('playwright');
const path = require('path');
const { pathToFileURL } = require('url');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 });
  const prototypePath = path.join(__dirname, 'transporthub-widget.html');
  await page.goto(pathToFileURL(prototypePath).href);
  await page.waitForTimeout(350);
  const messageLayout = await page.evaluate(() => {
    const content = document.querySelector('.content').getBoundingClientRect();
    const view = document.querySelector('.messages-view').getBoundingClientRect();
    const composer = document.querySelector('.chat-composer').getBoundingClientRect();
    return { contentBottom: content.bottom, viewBottom: view.bottom, composerBottom: composer.bottom };
  });
  if (messageLayout.composerBottom > messageLayout.contentBottom + 1) {
    throw new Error(`Message composer is clipped: ${JSON.stringify(messageLayout)}`);
  }
  const composerText = await page.locator('.chat-composer').innerText();
  if (/拖拽|粘贴|换行|全部电脑|选择文件/.test(composerText)) {
    throw new Error(`Composer still contains explanatory copy: ${composerText}`);
  }
  if (await page.locator('.attach-button svg').count() !== 1 || await page.locator('.send-icon-button svg').count() !== 1) {
    throw new Error('Minimal composer icons are missing.');
  }
  await page.screenshot({ path: path.join(__dirname, 'transporthub-widget-main.png') });
  await page.screenshot({ path: path.join(__dirname, 'transporthub-widget-text.png') });

  await page.click('.message-bubble.copyable');
  const copyToast = await page.locator('#toast').textContent();
  if (!copyToast.includes('已复制文字')) {
    throw new Error(`Text-copy feedback is missing: ${copyToast}`);
  }

  await page.click('.file-bubble');
  const fileToast = await page.locator('#toast').textContent();
  if (!fileToast.includes('打开文件夹并选中')) {
    throw new Error(`File-reveal feedback is missing: ${fileToast}`);
  }

  await page.evaluate(() => {
    const data = new DataTransfer();
    data.setData('text/plain', 'https://images.example.com/pasted-image.png');
    document.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
  });
  if (await page.locator('.link-bubble[data-link="https://images.example.com/pasted-image.png"]').count() !== 1) {
    throw new Error('Pasted image URL did not create a timeline link card.');
  }

  await page.evaluate(() => {
    const data = new DataTransfer();
    data.items.add(new File(['prototype'], 'wechat-image.png', { type: 'image/png' }));
    document.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
  });
  if (await page.locator('[data-file="粘贴图片-演示.png"]').count() !== 1) {
    throw new Error('Pasted clipboard image did not create a timeline image card.');
  }
  await page.evaluate(() => document.getElementById('toast').classList.remove('show'));
  await page.click('[data-state="collapsed"]');
  await page.waitForTimeout(350);
  const collapsedLayout = await page.evaluate(() => {
    const widget = document.querySelector('.widget').getBoundingClientRect();
    const stage = document.querySelector('.stage').getBoundingClientRect();
    return { width: widget.width, height: widget.height, rightGap: stage.right - widget.right };
  });
  if (Math.abs(collapsedLayout.width - 40) > 1 || Math.abs(collapsedLayout.height - 40) > 1 || collapsedLayout.rightGap < 0) {
    throw new Error(`Collapsed button layout is invalid: ${JSON.stringify(collapsedLayout)}`);
  }
  await page.screenshot({ path: path.join(__dirname, 'transporthub-widget-collapsed.png') });

  await page.click('#titlebar');
  await page.waitForTimeout(350);
  if (await page.locator('.widget.collapsed').count()) {
    throw new Error('Collapsed button did not restore the unified window.');
  }
  await browser.close();
})();
