# Generated data

Unity シミュレーション等から生成される JSON / GeoJSON をこのディレクトリへ配置します。

生成物本体は Git 管理せず、再生成手順と必要な小型 fixture のみを管理します。

環境コスト道路ネットワークの標準成果物は、経路サーバー用の`<area>-environment-cost-server-bundle-v1/`です。ブラウザへ直接配信せず、サーバーが読み込んで経路APIの計算に使用します。生成・検証方法は[`docs/environment-cost-road-network-generation.md`](../../docs/environment-cost-road-network-generation.md)を参照してください。
