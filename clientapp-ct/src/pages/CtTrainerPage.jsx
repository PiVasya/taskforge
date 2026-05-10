import React from 'react';
import Layout from '../components/Layout';

export default function CtTrainerPage() {
  return (
    <Layout fullWidth>
      <div className="h-[calc(100vh-57px)] bg-white dark:bg-neutral-950">
        <iframe
          title="A1 Орфография — обучалка"
          src="/trainer.html"
          className="w-full h-full border-0 bg-white"
        />
      </div>
    </Layout>
  );
}
