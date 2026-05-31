export const Field = ({ label, children, hint }) => (
    <div className="field">
        {label && <label className="label">{label}</label>}
        {children}
        {hint && <div className="text-xs text-neutral-500 mt-1">{hint}</div>}
    </div>
);


export const Input = (p) => <input {...p} className={`input ${p.className || ''}`} />;
export const Textarea = (p) => <textarea {...p} className={`textarea ${p.className || ''}`} />;
export const Select = (p) => <select {...p} className={`select ${p.className || ''}`} />;
export const Button = ({ variant, intent, className = '', ...p }) => {
    const v = variant || intent || 'primary';
    return (
        <button
            {...p}
            className={`${v === 'primary' ? 'btn-primary' : v === 'outline' ? 'btn-outline' : 'btn-ghost'} ${className}`}
        />
    );
};
export const Card = ({ className = '', children, ...p }) => <div {...p} className={`card p-5 ${className}`}>{children}</div>;
export const Badge = ({ children, variant, intent, className = '' }) => {
    const v = variant || intent || 'secondary';
    const cls =
        v === 'success'
            ? 'badge-success'
            : v === 'danger' || v === 'destructive'
                ? 'badge-danger'
                : v === 'outline'
                    ? 'badge-outline'
                    : 'badge-secondary';
    return <span className={`badge ${cls} ${className}`}>{children}</span>;
};
