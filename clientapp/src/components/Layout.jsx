import React, { useEffect, useState } from 'react';
import { Moon, Sun, BookOpen, PanelsTopLeft } from 'lucide-react';
import { motion } from 'framer-motion';


export default function Layout({ children }) {
    const [dark, setDark] = useState(() => localStorage.getItem('theme') === 'dark');


    useEffect(() => {
        const cls = document.documentElement.classList;
        dark ? cls.add('dark') : cls.remove('dark');
        localStorage.setItem('theme', dark ? 'dark' : 'light');
    }, [dark]);


    return (
        <div className="min-h-screen">
            {/* subtle gradient bg */}
            <div className="pointer-events-none fixed inset-0 -z-10 bg-gradient-to-b from-brand-600/10 via-transparent to-transparent blur-2xl" />


            <header className="sticky top-0 z-20 border-b border-slate-200/70 dark:border-slate-800/70 backdrop-blur bg-white/70 dark:bg-slate-900/60">
                <div className="container-app flex h-16 items-center justify-between">
                    <div className="flex items-center gap-3">
                        <div className="h-9 w-9 rounded-xl bg-brand-600 text-white grid place-items-center shadow-soft"><PanelsTopLeft size={18} /></div>
                        <div className="font-semibold">TaskForge</div>
                        <span className="hidden sm:inline-flex items-center gap-2 text-sm text-slate-500 dark:text-slate-400 ml-2"><BookOpen size={16} /> Платформа задач</span>
                    </div>
                    <div className="flex items-center gap-2">
                        <button className="btn-ghost" onClick={() => setDark(v => !v)} aria-label="Toggle theme">
                            {dark ? <Sun size={18} /> : <Moon size={18} />}
                            <span className="hidden sm:inline">Тема</span>
                        </button>
                    </div>
                </div>
            </header>


            <main className="container-app py-8">
                <motion.div initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: .25 }}>
                    {children}
                </motion.div>
            </main>


            <footer className="mt-12 border-t border-slate-200/70 dark:border-slate-800/70">
                <div className="container-app py-6 text-sm text-slate-500 dark:text-slate-400">© {new Date().getFullYear()} TaskForge</div>
            </footer>
        </div>
    );
}