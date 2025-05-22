using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using OpenUtau.Core;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Render;
using OpenUtau.Core.SignalChain;
using NAudio.Wave;
using OpenUtau.Core.Format.MusicXMLSchema;
using OpenUtau.Core.Util;

namespace OpenUtau.Core.Controllers {
    [ApiController]
    [Route("api/[controller]")]
    public class ProjectController : ControllerBase {
        private static UProject? currentProject;

        [HttpGet]
        public IActionResult Get() {
            Console.WriteLine("Received GET request for project");
            return Ok(new {
                status = "ok",
                message = "OpenUtau HTTP API is running",
                currentProject = currentProject != null ? new {
                    name = currentProject.name,
                    filePath = currentProject.FilePath
                } : null
            });
        }

        [HttpPost("load")]
        public IActionResult LoadProject([FromBody] LoadProjectRequest request) {
            try {
                if (string.IsNullOrEmpty(request.FilePath)) {
                    return BadRequest(new { error = "文件路径不能为空" });
                }

                if (!System.IO.File.Exists(request.FilePath)) {
                    return NotFound(new { error = "文件不存在" });
                }

                // 如果已有项目，先卸载
                if (currentProject != null) {
                    DocManager.Inst.ExecuteCmd(new LoadProjectNotification(new UProject()));
                    // 清除缓存
                    PathManager.Inst.ClearCache();
                }

                // 加载新项目
                var project = Formats.ReadProject(new[] { request.FilePath });
                if (project == null) {
                    return BadRequest(new { error = "项目加载失败" });
                }

                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
                currentProject = project;

                return Ok(new {
                    status = "ok",
                    message = "项目加载成功",
                    project = new {
                        name = project.name,
                        filePath = project.FilePath
                    }
                });
            }
            catch (Exception ex) {
                Log.Error(ex, "加载项目时发生错误");
                return StatusCode(500, new { error = "加载项目时发生错误", details = ex.Message });
            }
        }

        [HttpPost("unload")]
        public IActionResult UnloadProject() {
            try {
                if (currentProject == null) {
                    return Ok(new { message = "当前没有加载的项目" });
                }

                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(new UProject()));
                currentProject = null;

                // 清除缓存
                PathManager.Inst.ClearCache();

                return Ok(new { status = "ok", message = "项目已卸载" });
            }
            catch (Exception ex) {
                Log.Error(ex, "卸载项目时发生错误");
                return StatusCode(500, new { error = "卸载项目时发生错误", details = ex.Message });
            }
        }

        [HttpPost("export")]
        public async Task<IActionResult> ExportWav([FromBody] ExportWavRequest request) {
            try {
                if (currentProject == null) {
                    return BadRequest(new { error = "没有加载的项目" });
                }

                if (string.IsNullOrEmpty(request.OutputPath)) {
                    return BadRequest(new { error = "输出路径不能为空" });
                }

                // 确保输出目录存在
                var outputDir = Path.GetDirectoryName(request.OutputPath);
                if (!string.IsNullOrEmpty(outputDir)) {
                    try {
                        if (!Directory.Exists(outputDir)) {
                            Directory.CreateDirectory(outputDir);
                        }
                        // 检查目录权限
                        var testFile = Path.Combine(outputDir, "test.tmp");
                        System.IO.File.WriteAllText(testFile, "test");
                        System.IO.File.Delete(testFile);
                    }
                    catch (UnauthorizedAccessException) {
                        return StatusCode(403, new { error = "没有权限访问输出目录", path = outputDir });
                    }
                    catch (Exception ex) {
                        return StatusCode(500, new { error = "创建输出目录失败", details = ex.Message });
                    }
                }

                // 执行导出
                var cancellation = new CancellationTokenSource();
                try {
                    RenderEngine engine = new RenderEngine(currentProject);
                    var projectMix = engine.RenderMixdown(DocManager.Inst.MainScheduler, ref cancellation, wait: true).Item1;
                    DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, $"Exporting to {request.OutputPath}."));

                    // 确保目标文件可写
                    if (System.IO.File.Exists(request.OutputPath)) {
                        try {
                            using (FileStream fs = System.IO.File.Open(request.OutputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                                fs.Close();
                            }
                        }
                        catch (IOException) {
                            return StatusCode(409, new { error = "输出文件被其他程序占用", path = request.OutputPath });
                        }
                    }

                    WaveFileWriter.CreateWaveFile16(request.OutputPath, new ExportAdapter(projectMix).ToMono(1, 0));
                    DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, $"Exported to {request.OutputPath}."));
                }
                catch (IOException ioe) {
                    var customEx = new MessageCustomizableException($"Failed to export {request.OutputPath}.", $"<translate:errors.failed.export>: {request.OutputPath}", ioe);
                    DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(customEx));
                    DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, $"Failed to export {request.OutputPath}."));
                    return StatusCode(500, new { error = "导出文件失败", details = ioe.Message });
                }
                catch (Exception e) {
                    var customEx = new MessageCustomizableException("Failed to render.", $"<translate:errors.failed.render>: {request.OutputPath}", e);
                    DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(customEx));
                    DocManager.Inst.ExecuteCmd(new ProgressBarNotification(0, $"Failed to render."));
                    return StatusCode(500, new { error = "渲染失败", details = e.Message });
                }

                return Ok(new {
                    status = "ok",
                    message = "导出成功",
                    outputPath = request.OutputPath
                });
            }
            catch (Exception ex) {
                Log.Error(ex, "导出WAV时发生错误");
                return StatusCode(500, new { error = "导出WAV时发生错误", details = ex.Message });
            }
        }
    }

    public class LoadProjectRequest {
        public string FilePath { get; set; } = string.Empty;
    }

    public class ExportWavRequest {
        public string OutputPath { get; set; } = string.Empty;
    }
}
