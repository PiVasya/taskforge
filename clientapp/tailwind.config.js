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
                // Theme-aware palette.
                // Values are provided via CSS variables on <html> for each color theme.
                // Tailwind then supports classes like bg-brand-600, ring-brand-500/30, from-brand-600/10, etc.
                brand: {
                    50: 'rgb(var(--brand-50) / <alpha-value>)',
                    100: 'rgb(var(--brand-100) / <alpha-value>)',
                    200: 'rgb(var(--brand-200) / <alpha-value>)',
                    300: 'rgb(var(--brand-300) / <alpha-value>)',
                    400: 'rgb(var(--brand-400) / <alpha-value>)',
                    500: 'rgb(var(--brand-500) / <alpha-value>)',
                    600: 'rgb(var(--brand-600) / <alpha-value>)',
                    700: 'rgb(var(--brand-700) / <alpha-value>)',
                    800: 'rgb(var(--brand-800) / <alpha-value>)',
                    900: 'rgb(var(--brand-900) / <alpha-value>)'
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