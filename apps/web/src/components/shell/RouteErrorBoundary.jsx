import React from 'react';

class RouteErrorBoundary extends React.Component {
  constructor(props) {
    super(props);
    this.state = { error: null };
  }

  static getDerivedStateFromError(error) {
    return { error };
  }

  componentDidCatch(error, info) {
    console.error('[TaskForge route]', error, info);
  }

  componentDidUpdate(previousProps) {
    if (previousProps.resetKey !== this.props.resetKey && this.state.error) {
      this.setState({ error: null });
    }
  }

  render() {
    if (!this.state.error) return this.props.children;

    return (
      <div className="rounded-3xl border border-rose-300/60 bg-rose-50/85 p-6 text-rose-950 shadow-soft backdrop-blur dark:border-rose-800/70 dark:bg-rose-950/45 dark:text-rose-100">
        <div className="text-lg font-semibold">Не удалось открыть этот раздел</div>
        <div className="mt-2 text-sm opacity-80">
          Остальная оболочка TaskForge продолжает работать. Перезагрузи только страницу, чтобы повторить загрузку раздела.
        </div>
        <button
          type="button"
          className="btn-primary mt-5"
          onClick={() => window.location.reload()}
        >
          Перезагрузить страницу
        </button>
      </div>
    );
  }
}

export default RouteErrorBoundary;
