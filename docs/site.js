'use strict';
const repositoryUrl = 'https://raw.githubusercontent.com/SgZincanya/Zincat/main/docs/index.json';
const copyButton = document.querySelector('#copy-url');
const copyStatus = document.querySelector('#copy-status');
const urlInput = document.querySelector('#repo-url');
copyButton.addEventListener('click', async () => {
  try {
    await navigator.clipboard.writeText(repositoryUrl);
    copyButton.querySelector('span').textContent = '已复制';
    copyStatus.textContent = '地址已复制。打开 VCC 的 Add Repository，粘贴后确认添加。';
  } catch {
    urlInput.focus(); urlInput.select();
    copyStatus.textContent = '已选中仓库地址，请按 Ctrl+C（Mac 使用 ⌘C）或长按复制。';
  }
});
fetch('./index.json', { cache: 'no-cache' })
  .then(response => { if (!response.ok) throw new Error('Listing unavailable'); return response.json(); })
  .then(listing => {
    const versions = Object.keys(listing.packages['io.github.sgzincanya.zincat'].versions)
      .filter(version => /^\d+\.\d+\.\d+$/.test(version))
      .sort((a, b) => { const left=a.split('.').map(Number), right=b.split('.').map(Number); return right[0]-left[0] || right[1]-left[1] || right[2]-left[2]; });
    if (!versions.length) return;
    const link=document.querySelector('#latest-version');
    link.textContent='v'+versions[0]+' · 最新版 ↗';
    link.href='https://github.com/SgZincanya/Zincat/releases/tag/v'+versions[0];
  })
  .catch(() => { /* Keep the releases link available if the listing cannot load. */ });
