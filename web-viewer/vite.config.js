import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { viteStaticCopy } from 'vite-plugin-static-copy';
export default defineConfig({
    plugins: [
        react(),
        viteStaticCopy({
            targets: [
                {
                    src: 'node_modules/three/examples/jsm/libs/draco/*',
                    dest: 'draco'
                }
            ]
        })
    ],
    server: {
        host: true,
        port: 5173
    },
    build: {
        target: 'es2020',
        sourcemap: false
    }
});
