// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#include "AppOfflineApplication.h"

#include <experimental/filesystem>
#include "HandleWrapper.h"

HRESULT AppOfflineApplication::CreateHandler(IHttpContext* pHttpContext, IREQUEST_HANDLER** pRequestHandler)
{
    try
    {
        *pRequestHandler = new AppOfflineHandler(pHttpContext, m_strAppOfflineContent);
    }
    CATCH_RETURN();

    return S_OK;
}

HRESULT AppOfflineApplication::OnAppOfflineFound()
{
    LARGE_INTEGER   li = {};

    HandleWrapper<InvalidHandleTraits> handle = CreateFile(m_appOfflineLocation.c_str(),
                        GENERIC_READ,
                        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                        nullptr,
                        OPEN_EXISTING,
                        FILE_ATTRIBUTE_NORMAL,
                        nullptr);

    RETURN_LAST_ERROR_IF(handle == INVALID_HANDLE_VALUE);

    RETURN_LAST_ERROR_IF(!GetFileSizeEx(handle, &li));

    if (li.HighPart != 0)
    {
        // > 4gb file size not supported
        // todo: log a warning at event log
        return E_INVALIDARG;
    }

    if (li.LowPart > 0)
    {
        DWORD bytesRead = 0;
        std::string pszBuff(li.LowPart + 1, '\0');

        RETURN_LAST_ERROR_IF(!ReadFile(handle, pszBuff.data(), li.LowPart, &bytesRead, NULL));
        pszBuff.resize(bytesRead);

        m_strAppOfflineContent = pszBuff;
    }

    return S_OK;
}

bool AppOfflineApplication::ShouldBeStarted(IHttpApplication& pApplication)
{
    return is_regular_file(GetAppOfflineLocation(pApplication));
}

REQUEST_NOTIFICATION_STATUS AppOfflineHandler::OnExecuteRequestHandler()
{
    HTTP_DATA_CHUNK   DataChunk;
    IHttpResponse* pResponse = m_pContext->GetResponse();

    DBG_ASSERT(pResponse);

    // Ignore failure hresults as nothing we can do
    // Set fTrySkipCustomErrors to true as we want client see the offline content
    pResponse->SetStatus(503, "Service Unavailable", 0, S_OK, nullptr, TRUE);
    pResponse->SetHeader("Content-Type",
        "text/html",
        static_cast<USHORT>(strlen("text/html")),
        FALSE
    );

    DataChunk.DataChunkType = HttpDataChunkFromMemory;
    DataChunk.FromMemory.pBuffer = m_strAppOfflineContent.data();
    DataChunk.FromMemory.BufferLength = static_cast<ULONG>(m_strAppOfflineContent.size());
    pResponse->WriteEntityChunkByReference(&DataChunk);

    return REQUEST_NOTIFICATION_STATUS::RQ_NOTIFICATION_FINISH_REQUEST;
}
