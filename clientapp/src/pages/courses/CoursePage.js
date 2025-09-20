import React, { useEffect, useState } from "react";
import { useParams } from "react-router-dom";
import { getAssignments, createAssignment } from '../../api/courses';

function CoursePage() {
    const { courseId } = useParams();
    const [assignments, setAssignments] = useState([]);
    const [title, setTitle] = useState("");
    const [description, setDescription] = useState("");

    useEffect(() => {
        async function load() {
            try {
                const data = await getAssignments(courseId);
                setAssignments(data);
            } catch (err) {
                console.error("Ошибка загрузки:", err.message);
            }
        }
        load();
    }, [courseId]);

    const handleCreate = async () => {
        try {
            const newAssignment = await createAssignment(courseId, { title, description });
            setAssignments([...assignments, newAssignment]);
            setTitle("");
            setDescription("");
        } catch (err) {
            alert(err.message);
        }
    };

    return (
        <div style={{ maxWidth: 600, margin: "0 auto" }}>
            <h2>Задания курса</h2>
            <ul>
                {assignments.map(a => (
                    <li key={a.id}>
                        <b>{a.title}</b> — {a.description}
                    </li>
                ))}
            </ul>
            <div>
                <h3>Добавить задание</h3>
                <input
                    type="text"
                    placeholder="Название"
                    value={title}
                    onChange={e => setTitle(e.target.value)}
                />
                <textarea
                    placeholder="Описание"
                    value={description}
                    onChange={e => setDescription(e.target.value)}
                />
                <button onClick={handleCreate}>Создать задание</button>
            </div>
        </div>
    );
}

export default CoursePage;
