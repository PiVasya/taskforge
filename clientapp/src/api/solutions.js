import axios from "axios";

const API = process.env.REACT_APP_API_BASE || "/api";

export async function runSolutionRich(payload) {
  // payload: { assignmentId, language, source, stdin?, timeLimitMs?, memoryLimitMb? }
  const { data } = await axios.post(`${API}/judge/run`, payload);
  return data;
}
