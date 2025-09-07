import type { Config } from 'tailwindcss';
import skeleton from '@skeletonlabs/skeleton';

export default {
content: ['./src/**/*.{html,js,svelte,ts}'],
theme: {
screens: {
sm: '40rem',
md: '48rem',
lg: '64rem',
xl: '80rem',
'2xl': '96rem'
}
},
plugins: [skeleton]
} satisfies Config;

