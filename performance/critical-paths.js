import http from 'k6/http';
import { check, sleep } from 'k6';

const apiBaseUrl = (__ENV.API_BASE_URL || '').replace(/\/$/, '');
const webBaseUrl = (__ENV.WEB_BASE_URL || '').replace(/\/$/, '');

if (!apiBaseUrl.startsWith('https://') || !webBaseUrl.startsWith('https://')) {
  throw new Error('API_BASE_URL and WEB_BASE_URL must be explicit HTTPS staging targets.');
}

export const options = {
  vus: Number(__ENV.VUS || 10),
  duration: __ENV.DURATION || '1m',
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<750', 'p(99)<1500'],
    checks: ['rate>0.99'],
  },
};

export default function () {
  const responses = http.batch([
    ['GET', `${webBaseUrl}/`, null, { tags: { surface: 'storefront' } }],
    ['GET', `${apiBaseUrl}/health/live`, null, { tags: { surface: 'liveness' } }],
    ['GET', `${apiBaseUrl}/health/ready`, null, { tags: { surface: 'readiness' } }],
    ['GET', `${apiBaseUrl}/api/categories`, null, { tags: { surface: 'catalog' } }],
    ['GET', `${apiBaseUrl}/api/products`, null, { tags: { surface: 'catalog' } }],
  ]);

  check(responses[0], { 'storefront responds': response => response.status === 200 });
  check(responses[1], { 'liveness is healthy': response => response.status === 200 });
  check(responses[2], { 'readiness is healthy': response => response.status === 200 });
  check(responses[3], { 'categories respond': response => response.status === 200 });
  check(responses[4], { 'products respond': response => response.status === 200 });
  sleep(1);
}
