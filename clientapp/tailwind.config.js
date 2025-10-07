/** @type {import('tailwindcss').Config} */
export default {
    darkMode: 'class',
    content: [
        './index.html',
        './src/**/*.{js,jsx,ts,tsx}'
    ],
    theme: {
        extend: {
            colors: {
                brand: {
                    50: '#f2fbff',
                    100: '#e6f6ff',
                    200: '#ccecff',
                    300: '#99d9ff',
                    400: '#66c6ff',
                    500: '#33b3ff',
                    600: '#0ea5e9', // основной
                    700: '#0b7bb0',
                    800: '#095f88',
                    900: '#074766'
                }
            },
            boxShadow: {
                soft: '0 10px 30px rgba(2, 6, 23, 0.08)'
            },
            borderRadius: {
                xl2: '1.25rem'
            }
        }
    },
    plugins: []
}