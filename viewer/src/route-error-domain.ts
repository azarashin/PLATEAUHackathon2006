export function routeErrorMessage(code: string): string {
  switch (code) {
    case 'SNAP_NOT_FOUND':
      return '許容距離内に歩行可能な道路がないため、地図上の道路付近を選び直してください。'
    case 'OUTSIDE_COVERAGE':
      return '選択地点が計算済み範囲外のため、対象範囲内の地点を選び直してください。'
    case 'TIMESTAMP_NOT_AVAILABLE':
      return '選択した日時の解析結果がありません。計算済み日時を選び直してください。'
    case 'ROUTE_NOT_FOUND':
      return '出発地と目的地の間に歩行可能な接続がないため、地点を選び直してください。'
    default:
      return '経路データを取得できませんでした。時間をおいて再度お試しください。'
  }
}
