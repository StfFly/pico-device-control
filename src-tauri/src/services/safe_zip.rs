use std::fs::{self, File};
use std::io;
use std::path::{Component, Path, PathBuf};
use zip::ZipArchive;

const MAX_ENTRIES: usize = 20_000;
const MAX_TOTAL_UNCOMPRESSED_BYTES: u64 = 16 * 1024 * 1024 * 1024; // 16GB

/// 安全解压 ZIP，防御 Zip Slip 路径穿越与 Zip 炸弹
pub fn extract_zip_safe(zip_path: &Path, dest_dir: &Path) -> Result<(), String> {
    fs::create_dir_all(dest_dir).map_err(|e| format!("创建解压目录失败: {}", e))?;
    let canonical_dest = dest_dir
        .canonicalize()
        .unwrap_or_else(|_| dest_dir.to_path_buf());

    let file = File::open(zip_path).map_err(|e| format!("打开压缩包失败: {}", e))?;
    let mut archive = ZipArchive::new(file).map_err(|e| format!("无效的 ZIP 压缩包: {}", e))?;

    if archive.len() > MAX_ENTRIES {
        return Err(format!("ZIP 文件条目数超限 ({} > {})", archive.len(), MAX_ENTRIES));
    }

    let mut total_bytes: u64 = 0;

    for i in 0..archive.len() {
        let mut entry = archive
            .by_index(i)
            .map_err(|e| format!("读取 ZIP 条目失败: {}", e))?;

        let raw_name = entry.name().replace('\\', "/");
        if raw_name.is_empty() {
            continue;
        }

        // 校验路径段，拦截 ..、绝对路径和盘符前缀
        let rel_path = Path::new(&raw_name);
        let mut clean_rel = PathBuf::new();
        for comp in rel_path.components() {
            match comp {
                Component::Normal(part) => clean_rel.push(part),
                Component::CurDir => {}
                Component::ParentDir | Component::RootDir | Component::Prefix(_) => {
                    return Err(format!("检测到非法 ZIP 路径穿越条目: {}", raw_name));
                }
            }
        }

        if clean_rel.as_os_str().is_empty() {
            continue;
        }

        let out_path = canonical_dest.join(&clean_rel);
        if !out_path.starts_with(&canonical_dest) {
            return Err(format!("检测到越界 ZIP 解压目标: {}", raw_name));
        }

        if entry.is_dir() || raw_name.ends_with('/') {
            fs::create_dir_all(&out_path).map_err(|e| format!("创建子目录失败: {}", e))?;
        } else {
            total_bytes = total_bytes.saturating_add(entry.size());
            if total_bytes > MAX_TOTAL_UNCOMPRESSED_BYTES {
                return Err("ZIP 解压总大小超过安全上限".to_string());
            }

            if let Some(parent) = out_path.parent() {
                fs::create_dir_all(parent).map_err(|e| format!("创建父目录失败: {}", e))?;
            }
            let mut outfile =
                File::create(&out_path).map_err(|e| format!("写入解压文件失败: {}", e))?;
            io::copy(&mut entry, &mut outfile).map_err(|e| format!("解压文件内容失败: {}", e))?;
        }
    }

    Ok(())
}
