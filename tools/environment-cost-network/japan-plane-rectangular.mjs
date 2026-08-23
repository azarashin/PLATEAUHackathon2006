const SEMI_MAJOR_AXIS_METERS = 6_378_137
const INVERSE_FLATTENING = 298.257222101
const SCALE_FACTOR = 0.9999
const RADIANS = Math.PI / 180

const ZONE_ORIGINS = new Map([
  [1, [33, 129.5]], [2, [33, 131]], [3, [36, 132 + 10 / 60]],
  [4, [33, 133.5]], [5, [36, 134 + 20 / 60]], [6, [36, 136]],
  [7, [36, 137 + 10 / 60]], [8, [36, 138.5]], [9, [36, 139 + 50 / 60]],
  [10, [40, 140 + 50 / 60]], [11, [44, 140.25]], [12, [44, 142.25]],
  [13, [44, 144.25]], [14, [26, 142]], [15, [26, 127.5]],
  [16, [26, 124]], [17, [26, 131]], [18, [20, 136]], [19, [26, 154]],
])

function coefficients() {
  const n = 1 / (2 * INVERSE_FLATTENING - 1)
  const powers = [1]
  for (let index = 1; index <= 6; index += 1) powers[index] = powers[index - 1] * n
  const A = SCALE_FACTOR * SEMI_MAJOR_AXIS_METERS / (1 + n) * (1 + powers[2] / 4 + powers[4] / 64 + powers[6] / 256)
  const alpha = [
    0,
    powers[1] / 2 - 2 * powers[2] / 3 + 5 * powers[3] / 16 + 41 * powers[4] / 180 - 127 * powers[5] / 288 + 7891 * powers[6] / 37800,
    13 * powers[2] / 48 - 3 * powers[3] / 5 + 557 * powers[4] / 1440 + 281 * powers[5] / 630 - 1983433 * powers[6] / 1935360,
    61 * powers[3] / 240 - 103 * powers[4] / 140 + 15061 * powers[5] / 26880 + 167603 * powers[6] / 181440,
    49561 * powers[4] / 161280 - 179 * powers[5] / 168 + 6601661 * powers[6] / 7257600,
    34729 * powers[5] / 80640 - 3418889 * powers[6] / 1995840,
    212378941 * powers[6] / 319334400,
  ]
  const beta = [
    0,
    powers[1] / 2 - 2 * powers[2] / 3 + 37 * powers[3] / 96 - powers[4] / 360 - 81 * powers[5] / 512 + 96199 * powers[6] / 604800,
    powers[2] / 48 + powers[3] / 15 - 437 * powers[4] / 1440 + 46 * powers[5] / 105 - 1118711 * powers[6] / 3870720,
    17 * powers[3] / 480 - 37 * powers[4] / 840 - 209 * powers[5] / 4480 + 5569 * powers[6] / 90720,
    4397 * powers[4] / 161280 - 11 * powers[5] / 504 - 830251 * powers[6] / 7257600,
    4583 * powers[5] / 161280 - 108847 * powers[6] / 3991680,
    20648693 * powers[6] / 638668800,
  ]
  const delta = [
    0,
    2 * powers[1] - 2 * powers[2] / 3 - 2 * powers[3] + 116 * powers[4] / 45 + 26 * powers[5] / 45 - 2854 * powers[6] / 675,
    7 * powers[2] / 3 - 8 * powers[3] / 5 - 227 * powers[4] / 45 + 2704 * powers[5] / 315 + 2323 * powers[6] / 945,
    56 * powers[3] / 15 - 136 * powers[4] / 35 - 1262 * powers[5] / 105 + 73814 * powers[6] / 2835,
    4279 * powers[4] / 630 - 332 * powers[5] / 35 - 399572 * powers[6] / 14175,
    4174 * powers[5] / 315 - 144838 * powers[6] / 6237,
    601676 * powers[6] / 22275,
  ]
  return { n, A, alpha, beta, delta }
}

const SERIES = coefficients()

function origin(zoneId) {
  const value = ZONE_ORIGINS.get(zoneId)
  if (!value) throw new RangeError(`Japan plane rectangular zone must be 1..19: ${zoneId}`)
  return value.map((degrees) => degrees * RADIANS)
}

function conformalLatitudeTerm(latitude, n) {
  const coefficient = 2 * Math.sqrt(n) / (1 + n)
  return Math.sinh(Math.atanh(Math.sin(latitude)) - coefficient * Math.atanh(coefficient * Math.sin(latitude)))
}

function originNorthing(latitudeOrigin) {
  const t = conformalLatitudeTerm(latitudeOrigin, SERIES.n)
  const xi = Math.atan(t)
  let value = xi
  for (let order = 1; order <= 6; order += 1) value += SERIES.alpha[order] * Math.sin(2 * order * xi)
  return SERIES.A * value
}

export function geographicToPlane([longitudeDegrees, latitudeDegrees], zoneId) {
  if (!Number.isFinite(longitudeDegrees) || !Number.isFinite(latitudeDegrees)) throw new TypeError('Geographic coordinate must be finite [longitude, latitude]')
  const [latitudeOrigin, longitudeOrigin] = origin(zoneId)
  const latitude = latitudeDegrees * RADIANS
  const longitudeDelta = longitudeDegrees * RADIANS - longitudeOrigin
  const t = conformalLatitudeTerm(latitude, SERIES.n)
  const xiPrime = Math.atan2(t, Math.cos(longitudeDelta))
  const etaPrime = Math.atanh(Math.sin(longitudeDelta) / Math.sqrt(1 + t ** 2))
  let xi = xiPrime
  let eta = etaPrime
  for (let order = 1; order <= 6; order += 1) {
    xi += SERIES.alpha[order] * Math.sin(2 * order * xiPrime) * Math.cosh(2 * order * etaPrime)
    eta += SERIES.alpha[order] * Math.cos(2 * order * xiPrime) * Math.sinh(2 * order * etaPrime)
  }
  return {
    northingMeters: SERIES.A * xi - originNorthing(latitudeOrigin),
    eastingMeters: SERIES.A * eta,
  }
}

export function planeToGeographic({ northingMeters, eastingMeters }, zoneId) {
  if (!Number.isFinite(northingMeters) || !Number.isFinite(eastingMeters)) throw new TypeError('Plane coordinate must contain finite northingMeters and eastingMeters')
  const [latitudeOrigin, longitudeOrigin] = origin(zoneId)
  const xi = (northingMeters + originNorthing(latitudeOrigin)) / SERIES.A
  const eta = eastingMeters / SERIES.A
  let xiPrime = xi
  let etaPrime = eta
  for (let order = 1; order <= 6; order += 1) {
    xiPrime -= SERIES.beta[order] * Math.sin(2 * order * xi) * Math.cosh(2 * order * eta)
    etaPrime -= SERIES.beta[order] * Math.cos(2 * order * xi) * Math.sinh(2 * order * eta)
  }
  const conformalLatitude = Math.asin(Math.sin(xiPrime) / Math.cosh(etaPrime))
  let latitude = conformalLatitude
  for (let order = 1; order <= 6; order += 1) latitude += SERIES.delta[order] * Math.sin(2 * order * conformalLatitude)
  const longitude = longitudeOrigin + Math.atan2(Math.sinh(etaPrime), Math.cos(xiPrime))
  return [longitude / RADIANS, latitude / RADIANS]
}

export function geographicToUnityLocal(coordinate, referenceCoordinate, zoneId) {
  const projected = geographicToPlane(coordinate, zoneId)
  const reference = geographicToPlane(referenceCoordinate, zoneId)
  return [projected.eastingMeters - reference.eastingMeters, 0, projected.northingMeters - reference.northingMeters]
}

export function unityLocalToGeographic([eastMeters, upMeters, northMeters], referenceCoordinate, zoneId) {
  if (![eastMeters, upMeters, northMeters].every(Number.isFinite)) throw new TypeError('Unity EUN coordinate must be finite [east, up, north]')
  const reference = geographicToPlane(referenceCoordinate, zoneId)
  return planeToGeographic({
    northingMeters: reference.northingMeters + northMeters,
    eastingMeters: reference.eastingMeters + eastMeters,
  }, zoneId)
}
