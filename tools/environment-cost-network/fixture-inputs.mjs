const HOURS = [8, 9]
const TIMESTAMPS = HOURS.map((hour) => `2025-08-01T${String(hour).padStart(2, '0')}:00:00+09:00`)

function hourly(walkingSeconds, shadeRatios, status, exclusionReason) {
  return HOURS.map((hour, index) => ({
    hour,
    timestamp: TIMESTAMPS[index],
    status,
    exclusionReason,
    sunElevationDegrees: 35 + index * 10,
    shadeRatio: shadeRatios[index],
    solarExposureSeconds: shadeRatios[index] === null ? null : walkingSeconds * (1 - shadeRatios[index]),
  }))
}

export function createFixtureInputs() {
  const center = [139.736043, 35.690470]
  const first = [139.7357, 35.6902]
  const second = [139.7360, 35.6904]
  const third = [139.7363, 35.6906]
  const graph = {
    schemaVersion: 'pedestrian-road-network-1.0',
    areaId: 'ichigaya-integration-fixture',
    generatedAt: '2026-08-23T06:00:00Z',
    graphFingerprintSha256: '1'.repeat(64),
    extent: { center, radiusMeters: 100 },
    coordinateSystems: {
      geographic: { epsg: 4326, axisOrder: ['longitude', 'latitude'] },
      unity: {
        japanPlaneRectangularZoneId: 9,
        epsg: 6677,
        coordinateSystem: 'EUN',
        referencePointGeographic: center,
      },
    },
    walking: { defaultSpeedMetersPerSecond: 1.4 },
    nodes: [
      { id: 'osm-node-1001', osmNodeId: 1001, coordinate: first },
      { id: 'osm-node-1002', osmNodeId: 1002, coordinate: second },
      { id: 'osm-node-1003', osmNodeId: 1003, coordinate: third },
    ],
    edges: [
      {
        id: 'osm-way-101-0:forward', physicalEdgeId: 'osm-way-101-0',
        sourceEdgeIds: ['osm-way-101-0', 'osm-way-102-0'], osmWayIds: [101, 102], highways: ['footway'],
        fromNodeId: 'osm-node-1001', toNodeId: 'osm-node-1002', direction: 'forward',
        coordinates: [first, second], lengthMeters: 140, walkingSeconds: 100,
      },
      {
        id: 'osm-way-101-0:backward', physicalEdgeId: 'osm-way-101-0',
        sourceEdgeIds: ['osm-way-101-0', 'osm-way-102-0'], osmWayIds: [101, 102], highways: ['footway'],
        fromNodeId: 'osm-node-1002', toNodeId: 'osm-node-1001', direction: 'backward',
        coordinates: [second, first], lengthMeters: 140, walkingSeconds: 100,
      },
      {
        id: 'osm-way-103-0:forward', physicalEdgeId: 'osm-way-103-0',
        sourceEdgeIds: ['osm-way-103-0'], osmWayIds: [103], highways: ['steps'],
        fromNodeId: 'osm-node-1002', toNodeId: 'osm-node-1003', direction: 'forward',
        coordinates: [second, third], lengthMeters: 70, walkingSeconds: 50,
      },
    ],
  }
  const environment = {
    schemaVersion: 'environment-cost-analysis-0.2',
    status: 'completed',
    analysisKey: '2'.repeat(64),
    resultFingerprintSha256: '3'.repeat(64),
    areaId: graph.areaId,
    generatedAt: '2026-08-23T06:30:00Z',
    center,
    radiusMeters: 100,
    coordinateZoneId: 9,
    settings: {
      date: '2025-08-01', timezone: 'Asia/Tokyo', hours: HOURS,
      sampleSpacingMeters: 25, pedestrianHeightMeters: 1.5, walkingSpeedMetersPerSecond: 1.4,
    },
    edges: [
      {
        id: 'osm-way-101-0', walkingSeconds: 100,
        sampleCount: 2, validSampleCount: 2, noGroundSampleCount: 0,
        hourly: hourly(100, [0.25, 0.5], 'available', null),
      },
      {
        id: 'osm-way-102-0', walkingSeconds: 100,
        sampleCount: 1, validSampleCount: 0, noGroundSampleCount: 1,
        hourly: hourly(100, [null, null], 'missing', 'road-surface-not-found'),
      },
    ],
  }
  return { graph, environment }
}
