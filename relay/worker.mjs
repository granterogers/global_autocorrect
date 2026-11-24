// D179: the Cloudflare Worker entry point. Everything it does is in learning.mjs.
import { handle } from './learning.mjs';

export default {
  fetch: (request, env) => handle(request, env),
};
