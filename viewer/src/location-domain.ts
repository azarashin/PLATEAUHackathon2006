export type Coordinate = [number, number]

export interface DemoArea {
  id: string
  name: string
  centerName: string
  center: Coordinate
  radiusMeters: number
  availableTimestamps: string[]
}

export const demoAreas: DemoArea[] = [
  { id: 'kyoto', name: '京都市', centerName: '京都駅', center: [135.75877, 34.98535], radiusMeters: 4000, availableTimestamps: [] },
  { id: 'maizuru', name: '舞鶴市', centerName: '東舞鶴駅', center: [135.3946946, 35.4685404], radiusMeters: 4000, availableTimestamps: [] },
  { id: 'fujisawa', name: '藤沢市', centerName: '藤沢駅', center: [139.487293, 35.338882], radiusMeters: 4000, availableTimestamps: [] },
  { id: 'saitama', name: 'さいたま市', centerName: '大宮区・天沼町2丁目', center: [139.640025, 35.900757], radiusMeters: 4000, availableTimestamps: [] },
  { id: 'ichigaya-venue', name: '市ヶ谷周辺', centerName: '五番町グランドビル', center: [139.736043, 35.69047], radiusMeters: 4000, availableTimestamps: ['2025-08-01T12:00:00+09:00'] },
]

export function haversineMeters(left: Coordinate, right: Coordinate): number {
  const radians = (degrees: number) => degrees * Math.PI / 180
  const latitudeDelta = radians(right[1] - left[1])
  const longitudeDelta = radians(right[0] - left[0])
  const value = Math.sin(latitudeDelta / 2) ** 2
    + Math.cos(radians(left[1])) * Math.cos(radians(right[1])) * Math.sin(longitudeDelta / 2) ** 2
  return 6371008.8 * 2 * Math.atan2(Math.sqrt(value), Math.sqrt(1 - value))
}

export function findCoveredArea(coordinate: Coordinate, areas = demoAreas): DemoArea | undefined {
  return areas.find((area) => haversineMeters(coordinate, area.center) <= area.radiusMeters)
}

export function shouldDisplayDataset(selectedAreaId: string, datasetAreaId: string): boolean {
  return selectedAreaId === datasetAreaId
}

export function geolocationErrorMessage(code: number): string {
  const messages: Record<number, string> = {
    1: '現在位置の利用が許可されませんでした。ブラウザの権限設定を確認してください。',
    2: '現在位置を取得できませんでした。端末の位置情報設定を確認してください。',
    3: '現在位置の取得がタイムアウトしました。再度お試しください。',
  }
  return messages[code] ?? '現在位置の取得中に不明なエラーが発生しました。'
}
