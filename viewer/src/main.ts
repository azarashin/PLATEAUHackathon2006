import './style.css'

const app = document.querySelector<HTMLDivElement>('#app')

if (!app) {
  throw new Error('Application root #app was not found.')
}

app.innerHTML = `
  <main class="shell">
    <section class="hero" aria-labelledby="page-title">
      <p class="eyebrow">Environmental Cost Route Map Viewer</p>
      <h1 id="page-title">環境コスト経路マップビューア</h1>
      <p class="lead">
        都市環境を計算するシミュレーターと、結果を利用する軽量 Viewer をつなぐための開発基盤です。
      </p>
      <div class="status" role="status">
        <span class="status__dot" aria-hidden="true"></span>
        Viewer の起動基盤は準備できています
      </div>
    </section>

    <section class="preview" aria-labelledby="next-step-title">
      <div class="preview__canvas" aria-hidden="true">
        <div class="preview__grid"></div>
        <div class="preview__route preview__route--primary"></div>
        <div class="preview__route preview__route--secondary"></div>
      </div>
      <div class="preview__content">
        <p class="eyebrow">Next milestone · Issue #10</p>
        <h2 id="next-step-title">地図と2つのコストモードをここに実装します</h2>
        <ul>
          <li>日陰：高いほど望ましいポジティブ要因</li>
          <li>内水：高いほど危険なネガティブ要因</li>
        </ul>
        <p class="note">現在は開発環境確認用のプレースホルダーです。</p>
      </div>
    </section>
  </main>
`
