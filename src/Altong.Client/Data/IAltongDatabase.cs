using Microsoft.Data.Sqlite;

namespace Altong.Client.Data;

/// <summary>
/// 알통 로컬 SQLite 데이터베이스 연결 및 초기화 계약.
/// 단위 테스트 시 인메모리 DB나 Fake 주입을 지원합니다.
/// </summary>
public interface IAltongDatabase : IDisposable
{
    /// <summary>
    /// 새로운 활성 DB 연결 객체를 생성하고 오픈합니다.
    /// </summary>
    SqliteConnection CreateOpenConnection();

    /// <summary>
    /// 테이블 스키마 및 인덱스를 생성하고 WAL 모드를 활성화합니다.
    /// </summary>
    void Initialize();
}
