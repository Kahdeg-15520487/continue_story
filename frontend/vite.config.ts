import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

export default defineConfig({
	plugins: [sveltekit()],
	server: {
		host: '0.0.0.0',
		allowedHosts: ['cs.minhnguyenle.work'],
		proxy: {
			'/api': {
				target: process.env.API_PROXY_TARGET || 'http://api:5000',
				changeOrigin: true
			}
		}
	}
});
