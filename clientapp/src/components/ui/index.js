export const Field = ({ label, children, hint }) => (
    <div className="field">
        {label && <label className="label">{label}</label>}
        {children}
        {hint && <div className="text-xs text-slate-500 mt-1">{hint}</div>}
    </div>
);


export const Input = (p) => <input {...p} className={`input ${p.className || ''}`} />;
export const Textarea = (p) => <textarea {...p} className={`textarea ${p.className || ''}`} />;
export const Select = (p) => <select {...p} className={`select ${p.className || ''}`} />;
export const Button = ({ variant = 'primary', className = '', ...p }) => (
    <button {...p} className={`${variant === 'primary' ? 'btn-primary' : variant === 'outline' ? 'btn-outline' : 'btn-ghost'} ${className}`} />
);
export const Card = ({ className = '', children }) => <div className={`card p-5 ${className}`}>{children}</div>;
export const Badge = ({ children, className = '' }) => <span className={`badge ${className}`}>{children}</span>;
