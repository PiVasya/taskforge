using Microsoft.AspNetCore.Mvc;

namespace testapplication.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class NoteController : ControllerBase
    {
        [HttpDelete("{id}")]
        public IActionResult Delete(int id)
        {
            Console.WriteLine("Записи шо-то там");
            return Ok($"Заметка с id {id} удалена (не по-настоящему)");
        }
    }
}
