import { Children, cloneElement, isValidElement, useId } from 'react';

function isFormControl(child) {
    if (!isValidElement(child)) return false;
    if (typeof child.type === 'string') {
        return ['input', 'textarea', 'select'].includes(child.type);
    }
    return child.type === Input || child.type === Textarea || child.type === Select;
}

export const Field = ({ label, children, hint }) => {
    const reactId = useId();
    const baseId = `tf-field-${reactId.replace(/[^a-zA-Z0-9_-]/g, '')}`;
    const hintId = hint ? `${baseId}-hint` : undefined;
    const childList = Children.toArray(children);
    const controlIndex = childList.findIndex(isFormControl);
    const control = controlIndex >= 0 ? childList[controlIndex] : null;
    const controlId = control?.props?.id || baseId;

    if (controlIndex >= 0) {
        const describedBy = [control.props['aria-describedby'], hintId].filter(Boolean).join(' ') || undefined;
        childList[controlIndex] = cloneElement(control, {
            id: controlId,
            'aria-describedby': describedBy,
        });
    }

    return (
        <div className="field">
            {label && <label className="label" htmlFor={controlIndex >= 0 ? controlId : undefined}>{label}</label>}
            {childList}
            {hint && <div id={hintId} className="text-xs text-neutral-500 mt-1">{hint}</div>}
        </div>
    );
};

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
