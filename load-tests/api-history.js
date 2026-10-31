import http from "k6/http";
import { check, sleep } from "k6";

export const options = {
  stages: [
    { duration: "5s", target: 10 },
    { duration: "50s", target: 10 },
    { duration: "5s", target: 0 },
  ],
  thresholds: {
    checks: ["rate>0.99"],
    http_req_duration: ["p(95)<1000"],
    http_req_failed: ["rate<0.01"],
  },
};

export default function () {
  const baseUrl = __ENV.BASE_URL || "http://localhost:8080";
  const response = http.get(`${baseUrl}/api/jobs?page=1&pageSize=20`, {
    tags: { name: "GET /api/jobs" },
  });

  check(response, {
    "job history returns 200": (result) => result.status === 200,
  });
  sleep(1);
}
